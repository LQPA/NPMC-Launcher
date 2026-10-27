using System.Runtime.InteropServices;

namespace NPMC.Launcher.Infrastructure.Accounts;

/// <summary>用 Windows DPAPI（CurrentUser）保护 refresh token；不引入额外 NuGet 包。</summary>
internal static class WindowsDpapi
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅支持 Windows");

        return Transform(plain, protect: true);
    }

    public static byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅支持 Windows");

        return Transform(protectedData, protect: false);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob
        {
            Size = input.Length,
            Data = Marshal.AllocHGlobal(input.Length),
        };

        try
        {
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);

            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref inputBlob, "NPMC Launcher token", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);

            if (!ok)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "DPAPI 调用失败");

            try
            {
                var result = new byte[output.Size];
                if (output.Size > 0)
                    Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputBlob.Data);
        }
    }
}
