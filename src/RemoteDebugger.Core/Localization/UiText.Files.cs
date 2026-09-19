namespace RemoteDebugger.Core;

public static partial class UiText
{
    public static string RemoteFolder => Get(nameof(RemoteFolder));
    public static string RemoteFolderPlaceholder => Get(nameof(RemoteFolderPlaceholder));
    public static string OpenRemoteFolder => Get(nameof(OpenRemoteFolder));
    public static string FileTransfers => Get(nameof(FileTransfers));
    public static string FileTransferReady => Get(nameof(FileTransferReady));
    public static string PreparingFileTransfer => Get(nameof(PreparingFileTransfer));
    public static string VerifyingFileTransfer => Get(nameof(VerifyingFileTransfer));
    public static string CalculatingTransferEta => Get(nameof(CalculatingTransferEta));
    public static string FileTransferNumbers => Get(nameof(FileTransferNumbers));
    public static string UploadingTo => Get(nameof(UploadingTo));
    public static string DownloadingTo => Get(nameof(DownloadingTo));
}
