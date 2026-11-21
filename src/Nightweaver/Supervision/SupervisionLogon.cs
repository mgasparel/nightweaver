using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Nightweaver.Supervision;

internal static class SupervisionLogon
{
    internal static string ReadIdentity()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var user = WindowsIdentity.GetCurrent();
        // AuthenticationId identifies the logon session, unlike TokenId (one token)
        // or the reusable terminal session number. Groups can omit the logon SID.
        var size = Marshal.SizeOf<TokenStatistics>();
        if (!GetTokenInformation(user.AccessToken, 10 /* TokenStatistics */, out var statistics, size, out var returned)
            || returned != size || statistics.AuthenticationId == 0)
            throw new InvalidDataException("Current logon identity unavailable; supervision tick suppressed.");
        return statistics.AuthenticationId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    // TOKEN_STATISTICS, including the complete buffer so native size validation
    // cannot read a partial LUID or reinterpret another token information class.
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        internal long TokenId;
        internal long AuthenticationId;
        internal long ExpirationTime;
        internal int TokenType;
        internal int ImpersonationLevel;
        internal uint DynamicCharged;
        internal uint DynamicAvailable;
        internal uint GroupCount;
        internal uint PrivilegeCount;
        internal long ModifiedId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out TokenStatistics statistics, int size, out int returned);
}
