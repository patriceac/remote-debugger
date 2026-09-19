namespace RemoteDebugger.Core;

public static partial class UiText
{
    public static string ClientUpToDate => Get(nameof(ClientUpToDate));
    public static string ClientUpdateProgress => Get(nameof(ClientUpdateProgress));
    public static string ReceivingFile => Get(nameof(ReceivingFile));
    public static string SendingFile => Get(nameof(SendingFile));
    public static string FileReceived => Get(nameof(FileReceived));
    public static string FileSent => Get(nameof(FileSent));
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
