using Microsoft.Win32.SafeHandles;
using NPMC.P2P.Contracts.NamedPipe;
using NPMC.P2P.DeviceIdentity.Windows;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

public sealed record PipeClientValidation(
    bool Accepted,
    string? ErrorCode,
    string? Message,
    ClientProcessInfo? Client)
{
    public static PipeClientValidation Accept(ClientProcessInfo client) =>
        new(true, null, null, client);

    public static PipeClientValidation Reject(string errorCode, string message) =>
        new(false, errorCode, message, null);
}

/// <summary>可替换的客户端准入校验（测试可注入宽松实现；生产默认严格）。</summary>
public interface IPipeClientValidator
{
    PipeClientValidation Validate(SafePipeHandle pipe);
}

/// <summary>
/// 生产校验：同用户、同会话、映像白名单、父进程必须是启动器、不能是服务端自身。
/// 任一读取失败即拒绝。
/// </summary>
public sealed class StrictPipeClientValidator : IPipeClientValidator
{
    private readonly PipeServerOptions _options;
    private readonly string _currentUserSid;
    private readonly int _currentSessionId;
    private readonly int _expectedParentPid;

    public StrictPipeClientValidator(PipeServerOptions options)
    {
        _options = options;
        _currentUserSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                          ?? throw new InvalidOperationException("无法获取当前用户 SID。");
        _currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        _expectedParentPid = options.ExpectedLauncherProcessId ?? Environment.ProcessId;
    }

    public PipeClientValidation Validate(SafePipeHandle pipe)
    {
        if (!ClientProcessInspector.TryGetClientProcessId(pipe, out var clientPid))
        {
            return PipeClientValidation.Reject(PipeErrorCodes.BadClient, "无法获取管道客户端 PID。");
        }

        if (!ClientProcessInspector.TryDescribe(clientPid, out var client, out var failure))
        {
            return PipeClientValidation.Reject(
                PipeErrorCodes.BadClient,
                $"无法读取客户端进程信息（{failure}）。");
        }

        if (client.ProcessId == Environment.ProcessId)
        {
            return PipeClientValidation.Reject(PipeErrorCodes.BadClient, "客户端不能是启动器自身。");
        }

        if (!_options.AllowedClientImages.Any(
                name => string.Equals(name, client.ImageName, StringComparison.OrdinalIgnoreCase)))
        {
            return PipeClientValidation.Reject(
                PipeErrorCodes.BadClient,
                $"客户端映像不在白名单：{client.ImageName}");
        }

        if (!string.Equals(client.UserSid, _currentUserSid, StringComparison.OrdinalIgnoreCase))
        {
            return PipeClientValidation.Reject(PipeErrorCodes.BadClient, "客户端不是当前 Windows 用户。");
        }

        if (client.SessionId != _currentSessionId)
        {
            return PipeClientValidation.Reject(PipeErrorCodes.BadClient, "客户端不在当前 Windows 会话。");
        }

        if (_options.RequireLauncherParent && client.ParentProcessId != _expectedParentPid)
        {
            return PipeClientValidation.Reject(
                PipeErrorCodes.BadClient,
                $"客户端父进程不是启动器（parent={client.ParentProcessId}, expected={_expectedParentPid}）。");
        }

        return PipeClientValidation.Accept(client);
    }
}
