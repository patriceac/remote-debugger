using RemoteDebugger;
using RemoteDebugger.Core;
using Xunit;

public sealed class RestartResumeStoreTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void RestartGrantRequiresNewBootSameBinaryAndUnexpiredTicket()
    {
        string root = Path.Combine(Path.GetTempPath(), "RemoteDebuggerRestartTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new Clock(); string boot = "first"; string binary = new('c', 64);
            bool bootReadable = true;
            var store = new RestartResumeStore(root, () => bootReadable ? boot : throw new IOException("Boot provider unavailable."), time);
            var issued = store.Create(new('a', 64), new('b', 64), binary);
            Assert.Null(store.Restore(binary));
            boot = "second";
            var restored = Assert.IsType<RestartAuthorization>(store.Restore(binary));
            Assert.Equal(Safety.Hash(issued.Ticket), restored.TicketHash);
            Assert.Equal(time.Now.AddHours(1), restored.ExpiresUtc);
            bootReadable = false; Assert.Null(store.Restore(binary)); bootReadable = true;
            Assert.Null(store.Restore(new('d', 64)));
            time.Now = time.Now.AddHours(1); Assert.Null(store.Restore(binary));
            store.Clear(removeStartup: false);
            Assert.False(File.Exists(Path.Combine(root, "restart-session.resume")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
