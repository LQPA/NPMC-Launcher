using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NPMC.P2P.DeviceIdentity.NamedPipe;

/// <summary>
/// 管道 ACL：只允许当前 Windows 用户，禁用继承 ACE。
/// 不使用默认 DACL（默认可能包含 Everyone / ANONYMOUS）。
/// </summary>
public static class PipeSecurityFactory
{
    public static PipeSecurity CreateCurrentUserOnly()
    {
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("无法获取当前 Windows 用户 SID。");

        var security = new PipeSecurity();
        security.SetOwner(sid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
