using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void NormalDesktopLaunchesShareIdentityRegardlessOfDataDirectory()
    {
        string normal = SingleInstance.IdentityFor("user-a", 1, false, null);
        Assert.Equal(normal, SingleInstance.IdentityFor("user-a", 1, false, @"C:\data\one"));
        Assert.Equal(normal, SingleInstance.IdentityFor("user-a", 1, false, @"D:\data\two"));
        Assert.Equal(normal, SingleInstance.IdentityFor("user-a", 1, true, null));
    }

    [Fact]
    public void OtherWindowsUsersAndSessionsHaveIndependentWorkspaces()
    {
        string normal = SingleInstance.IdentityFor("user-a", 1, false, null);
        Assert.NotEqual(normal, SingleInstance.IdentityFor("user-b", 1, false, null));
        Assert.NotEqual(normal, SingleInstance.IdentityFor("user-a", 2, false, null));
    }

    [Fact]
    public void IsolatedLabRootsCanRepresentTwoPcsInOneGuest()
    {
        string agent = SingleInstance.IdentityFor("user-a", 1, true, @"C:\lab\agent");
        string controller = SingleInstance.IdentityFor("user-a", 1, true, @"C:\lab\controller");
        Assert.NotEqual(agent, controller);
        Assert.Equal(agent, SingleInstance.IdentityFor("user-a", 1, true, @"c:\LAB\agent\"));
    }
}
