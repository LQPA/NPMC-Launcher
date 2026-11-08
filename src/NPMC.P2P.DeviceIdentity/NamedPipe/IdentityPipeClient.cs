using System.IO.Pipes;
using System.Security.Cryptography;
using NPMC.P2P.Contracts;
using NPMC.P2P.Contracts.NamedPipe;
using NPMC.P2P.Contracts.Signing;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

public sealed class PipeClientException : Exception
{
    public PipeClientException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Named Pipe 客户端。Gate 0-B 用它模拟 Java Agent；真正的 Java Agent 只需按同一帧格式实现。
/// </summary>
public sealed class IdentityPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly TimeSpan _requestTimeout;

    private IdentityPipeClient(NamedPipeClientStream stream, TimeSpan requestTimeout)
    {
        _stream = stream;
        _requestTimeout = requestTimeout;
    }

    public string? ServerNonce { get; private set; }

    public static async Task<IdentityPipeClient> ConnectRawAsync(
        string pipeName,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        return new IdentityPipeClient(stream, connectTimeout);
    }

    public static async Task<IdentityPipeClient> ConnectAsync(
        string pipeName,
        string sessionToken,
        TimeSpan connectTimeout,
        CancellationToken cancellationToken = default)
    {
        var client = await ConnectRawAsync(pipeName, connectTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await client.HandshakeAsync(sessionToken, cancellationToken).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new PipeClientException(
                    response.ErrorCode ?? PipeErrorCodes.BadSession,
                    response.ErrorMessage ?? "握手失败。");
            }

            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PipeResponse> HandshakeAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        var request = new PipeRequest
        {
            ProtocolVersion = NamedPipeProtocol.Version,
            Type = NamedPipeProtocol.Hello,
            SessionToken = sessionToken,
            RequestId = Guid.NewGuid().ToString("N"),
            ClientNonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(ChallengeRules.NonceBytes)),
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Success && !string.IsNullOrEmpty(response.ServerNonce))
        {
            ServerNonce = response.ServerNonce;
            CurrentSessionToken = sessionToken;
        }

        return response;
    }

    public async Task<PipeResponse> SignChallengeAsync(
        SignChallengeRequest payload,
        CancellationToken cancellationToken = default)
    {
        if (ServerNonce is null)
        {
            throw new InvalidOperationException("尚未完成 hello 握手。");
        }

        var request = new PipeRequest
        {
            ProtocolVersion = NamedPipeProtocol.Version,
            Type = NamedPipeProtocol.SignChallenge,
            SessionToken = CurrentSessionToken,
            RequestId = Guid.NewGuid().ToString("N"),
            ClientNonce = Base64Url.Encode(RandomNumberGenerator.GetBytes(ChallengeRules.NonceBytes)),
            ServerNonce = ServerNonce,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = payload,
        };

        return await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>底层发送，供 Agent 模拟器与负向测试使用。</summary>
    public async Task<PipeResponse> SendRequestAsync(
        PipeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        await PipeFraming.WriteMessageAsync(
                _stream,
                PipeJson.Serialize(request),
                NamedPipeProtocol.MaxMessageBytes,
                timeout.Token)
            .ConfigureAwait(false);

        var frame = await PipeFraming
            .ReadMessageAsync(_stream, NamedPipeProtocol.MaxMessageBytes, timeout.Token)
            .ConfigureAwait(false);

        if (frame is null)
        {
            throw new IOException("管道在响应前被关闭。");
        }

        return PipeJson.Deserialize<PipeResponse>(frame);
    }

    /// <summary>握手成功后由 <see cref="HandshakeAsync"/> 记录，避免调用方重复传参。</summary>
    private string CurrentSessionToken { get; set; } = string.Empty;

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
