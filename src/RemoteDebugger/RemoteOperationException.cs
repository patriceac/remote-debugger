namespace RemoteDebugger;

public sealed class RemoteOperationException(string code, string message) : InvalidOperationException($"{code}: {message}")
{
    public string Code { get; } = code;
}
