using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger;

internal static class InteractiveProcessLauncher
{
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public static int Start(int sessionId, string expectedUserSid, string executable, IReadOnlyList<string> arguments, string workingDirectory)
        => StartCore(sessionId, expectedUserSid, executable, arguments, workingDirectory, false);

    internal static int StartInputHelper(int sessionId, string expectedUserSid, string pipeName) =>
        StartCore(sessionId, expectedUserSid, SupportPlatformPaths.ServiceExecutable, ["--input-helper", pipeName], SupportPlatformPaths.InstallDirectory, true);

    private static int StartCore(int sessionId, string expectedUserSid, string executable, IReadOnlyList<string> arguments, string workingDirectory, bool inputHelper)
    {
        if (sessionId <= 0) throw new ArgumentException("An interactive session is required.");
        if (!WTSQueryUserToken((uint)sessionId, out var sessionToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot obtain the original interactive user token.");
        using (sessionToken)
        {
            using var identity = new WindowsIdentity(sessionToken.DangerousGetHandle());
            if (!string.Equals(identity.User?.Value, expectedUserSid, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Interactive session user changed during update.");
            using var serviceIdentity = WindowsIdentity.GetCurrent();
            if (inputHelper && !serviceIdentity.IsSystem) throw new UnauthorizedAccessException("Only the provisioned service can launch interactive input.");
            if (!DuplicateTokenEx(inputHelper ? serviceIdentity.AccessToken : sessionToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot duplicate the interactive user token.");
            using (primary)
            {
                if (inputHelper && !SetTokenInformation(primary, 12, ref sessionId, sizeof(int)))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot bind input to the authorized desktop session.");
                if (!CreateEnvironmentBlock(out IntPtr environment, primary, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the interactive user environment.");
                try
                {
                    var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
                    string command = string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
                    var mutableCommand = new StringBuilder(command);
                    if (!CreateProcessAsUser(primary, executable, mutableCommand, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out var process))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot relaunch Remote Debugger in the original interactive session.");
                    try { return checked((int)process.ProcessId); }
                    finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
                }
                finally { DestroyEnvironmentBlock(environment); }
            }
        }
    }

    private static string Quote(string argument)
    {
        if (argument.Length != 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"')) return argument;
        var value = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') value.Append('\\', slashes * 2 + 1).Append(character);
            else value.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return value.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint desiredAccess, IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, ref int information, int length);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
