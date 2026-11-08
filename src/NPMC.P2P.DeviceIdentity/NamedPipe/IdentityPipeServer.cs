using System.IO.Pipes;
using System.Security.Cryptography;
using NPMC.P2P.Contracts;
using NPMC.P2P.Contracts.NamedPipe;
using NPMC.P2P.Contracts.Signing;
using NPMC.P2P.Contracts.Validation;
using NPMC.P2P.DeviceIdentity.Identity;
using NPMC.P2P.DeviceIdentity.Windows;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

/// <summary>
/// Named Pipe 签名代理服务端。
///
/// 生命周期：P2P 游戏会话开始时创建，会话结束时 Dispose。
/// 每个连接：客户端准入 → hello（会话令牌）→ 服务端下发 server_nonce →
/// 仅接受 sign_challenge 请求 → 校验绑定/时间戳/重放/限流 → 签名。
/// </summary>
public sealed class IdentityPipeServer : IAsyncDisposable
{
    private readonly PipeServerOptions _options;
    private readonly IAuthChallengeSigner _signer;
    private readonly IPipeClientValidator _clientValidator;
    private readonly SlidingWindowRateLimiter _rateLimiter;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private bool _disposed;

    public IdentityPipeServer(
        PipeServerOptions options,
        IAuthChallengeSigner signer,
        IPipeClientValidator? clientValidator = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clientValidator = clientValidator ?? new StrictPipeClientValidator(options);
        _rateLimiter = new SlidingWindowRateLimiter(
            options.MaxSignaturesPerMinute,
            TimeSpan.FromMinutes(1));
    }

    public string PipeName => _options.PipeName;

    public bool IsRunning => _acceptTask is { IsCompleted: false };

    /// <summary>最近一次通过准入校验的客户端（供验收/诊断使用）。</summary>
    public ClientProcessInfo? LastAcceptedClient { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acceptTask is not null)
        {
            throw new InvalidOperationException("Named Pipe 服务端只能启动一次。");
        }

        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // 处理单个连接不能阻塞后续连接：Minecraft 会重试/并发打开管道，
                // 串行处理会让旧连接把命名管道实例占满，表现为 "all pipe instances are busy"。
                var connected = server;
                server = null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleConnectionAsync(connected, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        connected.Dispose();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // 客户端在握手前断开：继续监听下一个连接。
            }
            catch (Exception)
            {
                // 单连接异常不能拖垮服务端；安全失败，继续监听。
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        return NamedPipeServerStreamAcl.Create(
            _options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: Math.Max(1, _options.MaxServerInstances),
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityFactory.CreateCurrentUserOnly());
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var validation = _clientValidator.Validate(server.SafePipeHandle);
        if (!validation.Accepted)
        {
            await TrySendAsync(
                server,
                Error(string.Empty, validation.ErrorCode ?? PipeErrorCodes.BadClient, validation.Message ?? "客户端被拒绝。"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        LastAcceptedClient = validation.Client;

        byte[]? helloFrame;
        try
        {
            helloFrame = await ReadAsync(server, cancellationToken).ConfigureAwait(false);
        }
        catch (PipeProtocolException ex)
        {
            await TrySendAsync(server, Error(string.Empty, ex.ErrorCode, ex.Message), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (helloFrame is null)
        {
            return;
        }

        if (!PipeJson.TryDeserialize(helloFrame, out PipeRequest? hello) || hello is null)
        {
            await TrySendAsync(
                server,
                Error(string.Empty, PipeErrorCodes.InvalidRequest, "hello 不是合法 JSON。"),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var helloError = ValidateHello(hello);
        if (helloError is not null)
        {
            await TrySendAsync(server, Error(hello.RequestId, helloError.Value.Code, helloError.Value.Message), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var serverNonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(ChallengeRules.NonceBytes));
        await TrySendAsync(
            server,
            new PipeResponse
            {
                RequestId = hello.RequestId,
                Success = true,
                ServerNonce = serverNonce,
            },
            cancellationToken).ConfigureAwait(false);

        var usedRequestIds = new HashSet<string>(StringComparer.Ordinal);
        var usedClientNonces = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            byte[]? frame;
            try
            {
                frame = await ReadAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (PipeProtocolException ex)
            {
                await TrySendAsync(server, Error(string.Empty, ex.ErrorCode, ex.Message), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (frame is null)
            {
                return;
            }

            if (!PipeJson.TryDeserialize(frame, out PipeRequest? request) || request is null)
            {
                await TrySendAsync(
                    server,
                    Error(string.Empty, PipeErrorCodes.InvalidRequest, "请求不是合法 JSON。"),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = ProcessSignRequest(request, serverNonce, usedRequestIds, usedClientNonces);
            await TrySendAsync(server, response, cancellationToken).ConfigureAwait(false);

            // 安全类错误（协议/类型/会话）直接断开，避免继续试探。
            if (!response.Success && response.ErrorCode is
                PipeErrorCodes.ProtocolMismatch or
                PipeErrorCodes.UnknownType or
                PipeErrorCodes.BadSession or
                PipeErrorCodes.InvalidRequest or
                PipeErrorCodes.InvalidFrame)
            {
                return;
            }
        }
    }

    private (string Code, string Message)? ValidateHello(PipeRequest hello)
    {
        if (hello.ProtocolVersion != NamedPipeProtocol.Version)
        {
            return (PipeErrorCodes.ProtocolMismatch, $"协议版本不支持：{hello.ProtocolVersion}");
        }

        if (!string.Equals(hello.Type, NamedPipeProtocol.Hello, StringComparison.Ordinal))
        {
            return (PipeErrorCodes.UnknownType, $"首帧必须是 hello，收到：{hello.Type}");
        }

        if (!_options.Binding.IsValidToken(hello.SessionToken))
        {
            return (PipeErrorCodes.BadSession, "会话令牌无效。");
        }

        if (!IdentifierRules.IsValid(hello.RequestId))
        {
            return (PipeErrorCodes.InvalidRequest, "request_id 不合法。");
        }

        if (!ChallengeRules.IsValidNonce(hello.ClientNonce))
        {
            return (PipeErrorCodes.InvalidRequest, "client_nonce 必须是 32 字节 base64url。");
        }

        return null;
    }

    private PipeResponse ProcessSignRequest(
        PipeRequest request,
        string serverNonce,
        HashSet<string> usedRequestIds,
        HashSet<string> usedClientNonces)
    {
        if (request.ProtocolVersion != NamedPipeProtocol.Version)
        {
            return Error(request.RequestId, PipeErrorCodes.ProtocolMismatch, "协议版本不支持。");
        }

        if (!string.Equals(request.Type, NamedPipeProtocol.SignChallenge, StringComparison.Ordinal))
        {
            return Error(request.RequestId, PipeErrorCodes.UnknownType, $"请求类型不允许：{request.Type}");
        }

        if (!_options.Binding.IsValidToken(request.SessionToken))
        {
            return Error(request.RequestId, PipeErrorCodes.BadSession, "会话令牌无效。");
        }

        if (!IdentifierRules.IsValid(request.RequestId))
        {
            return Error(request.RequestId, PipeErrorCodes.InvalidRequest, "request_id 不合法。");
        }

        if (!string.Equals(request.ServerNonce, serverNonce, StringComparison.Ordinal))
        {
            return Error(request.RequestId, PipeErrorCodes.BadSession, "server_nonce 不匹配。");
        }

        if (!ChallengeRules.IsValidNonce(request.ClientNonce))
        {
            return Error(request.RequestId, PipeErrorCodes.InvalidRequest, "client_nonce 必须是 32 字节 base64url。");
        }

        if (!usedRequestIds.Add(request.RequestId))
        {
            return Error(request.RequestId, PipeErrorCodes.Replay, "request_id 重放。");
        }

        if (!usedClientNonces.Add(request.ClientNonce))
        {
            return Error(request.RequestId, PipeErrorCodes.Replay, "client_nonce 重放。");
        }

        var now = DateTimeOffset.UtcNow;
        var skew = Math.Abs(now.ToUnixTimeMilliseconds() - request.TimestampUnixMs);
        if (skew > _options.MaxClockSkew.TotalMilliseconds)
        {
            return Error(request.RequestId, PipeErrorCodes.StaleTimestamp, $"时间戳偏差过大：{skew} ms");
        }

        var payload = request.Payload;
        if (payload is null)
        {
            return Error(request.RequestId, PipeErrorCodes.InvalidRequest, "缺少 payload。");
        }

        if (!_options.Binding.Matches(
                payload.RoomId,
                payload.NodeId,
                payload.ProfileId,
                payload.GameSessionId))
        {
            return Error(request.RequestId, PipeErrorCodes.InvalidBinding, "房间/节点/档案/游戏会话绑定不匹配。");
        }

        if (!ChallengeRules.IsValidChallenge(payload.Challenge))
        {
            return Error(request.RequestId, PipeErrorCodes.InvalidChallenge, "挑战长度或编码不合法。");
        }

        if (!_rateLimiter.TryAcquire(now))
        {
            return Error(request.RequestId, PipeErrorCodes.RateLimited, "签名频率超过限制。");
        }

        try
        {
            // p2p:auth 场景下，签名里的 server_nonce 必须是 Gateway 下发的 gateway_nonce；
            // 普通 Gate 0-B 场景没有该字段，沿用管道握手 nonce。
            var authNonce = serverNonce;
            if (!string.IsNullOrEmpty(payload.GatewayNonce))
            {
                if (!ChallengeRules.IsValidNonce(payload.GatewayNonce))
                {
                    return Error(
                        request.RequestId,
                        PipeErrorCodes.InvalidChallenge,
                        "gateway_nonce 必须是 32 字节 base64url。");
                }

                authNonce = payload.GatewayNonce;
            }

            var statement = new AuthChallengePayload(
                payload.Challenge,
                request.ClientNonce,
                _signer.DeviceId,
                _options.Binding.GameSessionId,
                _options.Binding.NodeId,
                _options.Binding.ProfileId,
                _options.Binding.RoomId,
                authNonce,
                now.ToUnixTimeMilliseconds());

            return new PipeResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Result = _signer.SignAuthChallenge(statement),
            };
        }
        catch (Exception ex)
        {
            return Error(request.RequestId, PipeErrorCodes.SignFailed, ex.Message);
        }
    }

    private async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        try
        {
            return await PipeFraming
                .ReadMessageAsync(stream, NamedPipeProtocol.MaxMessageBytes, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PipeProtocolException(PipeErrorCodes.InvalidFrame, "读取超时。");
        }
    }

    private async Task TrySendAsync(
        NamedPipeServerStream server,
        PipeResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await PipeFraming.WriteMessageAsync(
                    server,
                    PipeJson.Serialize(response),
                    NamedPipeProtocol.MaxMessageBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static PipeResponse Error(string requestId, string code, string message) => new()
    {
        RequestId = requestId ?? string.Empty,
        Success = false,
        ErrorCode = code,
        ErrorMessage = message,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _cts.Dispose();
    }
}
