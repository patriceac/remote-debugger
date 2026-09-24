using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDebugger;

internal sealed class RunningImageSignatureCache
{
    private readonly object sync = new();
    private readonly Func<string, string?, AuthenticodeSignatureInfo> verify;
    private FileIdentity identity;
    private long writeTime, changeTime;
    private object? peer;
    private string? publisher;
    private AuthenticodeSignatureInfo? signature;

    internal RunningImageSignatureCache(Func<string, string?, AuthenticodeSignatureInfo>? verify = null) =>
        this.verify = verify ?? ((path, pin) => pin == null
            ? AuthenticodeVerifier.InspectForEnrollment(path) : AuthenticodeVerifier.VerifyPinnedTrusted(path, pin));

    internal AuthenticodeSignatureInfo Get(string path, string? expectedPublisher, object processIdentity)
    {
        lock (sync)
        {
            // Only used for identified running images. Lock during each check,
            // then release the file so an updater can replace it between calls.
            using var current = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!GetFileInformationByHandleEx(current.SafeFileHandle, 18, out FileIdentity currentIdentity, 24) ||
                (currentIdentity.Low == 0 && currentIdentity.High == 0) ||
                !GetFileInformationByHandleEx(current.SafeFileHandle, 0, out FileBasicInfo currentInfo, 40))
            {
                signature = null;
                return verify(path, expectedPublisher);
            }
            var now = DateTime.UtcNow;
            if (identity.Equals(currentIdentity) && writeTime == currentInfo.LastWriteTime && changeTime == currentInfo.ChangeTime && Equals(peer, processIdentity) &&
                publisher == expectedPublisher && signature != null && now >= signature.ValidFromUtc && now < signature.ValidUntilUtc)
                return signature;

            signature = null;
            var verified = verify(path, expectedPublisher);
            writeTime = currentInfo.LastWriteTime; changeTime = currentInfo.ChangeTime;
            identity = currentIdentity; peer = processIdentity; publisher = expectedPublisher; signature = verified;
            return verified;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdentity information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileBasicInfo information, uint size);
}
