using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ViewerKeyboardTests
{
    [Fact]
    public void BothExitChordsAcceptLeftAndRightModifiersButNormalF12PassesThrough()
    {
        Assert.True(RemoteKeyboardCapture.IsReleaseShortcut(0x7B, [0xA2, 0xA4]));
        Assert.True(RemoteKeyboardCapture.IsReleaseShortcut(0x7B, [0xA3, 0xA1]));
        Assert.False(RemoteKeyboardCapture.IsReleaseShortcut(0x7B, [0xA2]));
        Assert.False(RemoteKeyboardCapture.IsReleaseShortcut(0x7B, [0xA4, 0xA0]));
        Assert.False(RemoteKeyboardCapture.IsReleaseShortcut(0x24, [0xA2, 0xA4]));
    }

    [Fact]
    public void HomeAndRightModifiersRetainExtendedScanCodeFlagsOnPressAndRelease()
    {
        foreach (int vk in new[] { 0x24, 0x23, 0x25, 0x2E, 0x5B, 0xA3, 0xA5 }) Assert.True(Native.IsExtendedKey(vk));
        Assert.False(Native.IsExtendedKey(0xA0));
        Assert.Equal(9u, Native.KeyboardFlags(false, 0x47, true));
        Assert.Equal(11u, Native.KeyboardFlags(true, 0x47, true));
        Assert.Equal(0u, Native.KeyboardFlags(false, 0, false));
        Assert.Equal(2u, Native.KeyboardFlags(true, 0, false));
    }

    [Fact]
    public void SecureAttentionPreservesExplicitPolicyAndHasColdServiceStartupDeadline()
    {
        Assert.True(SecureAttention.PolicyAllowsService(null));
        Assert.True(SecureAttention.PolicyAllowsService(1));
        Assert.True(SecureAttention.PolicyAllowsService(3));
        Assert.False(SecureAttention.PolicyAllowsService(0));
        Assert.False(SecureAttention.PolicyAllowsService(2));
        Assert.False(SecureAttention.PolicyAllowsService("1"));
        Assert.Equal(SupportOperationTimeouts.InputSeconds("release"), SupportOperationTimeouts.InputSeconds("secureAttention"));
    }

    [Fact]
    public void ResourcePollingUsesDirectRoutesAndGpuUsesBusiestCombinedEngine()
    {
        Assert.True(MainForm.IsDirectResourceRoute("Direct LAN"));
        Assert.True(MainForm.IsDirectResourceRoute("Direct WAN"));
        Assert.False(MainForm.IsDirectResourceRoute("Relay"));
        Assert.False(MainForm.IsDirectResourceRoute(null));
        Assert.Equal(70d, ResourceCounters.GpuUsage([
            ("pid_1_luid_0x0_0x123_phys_0_eng_0_engtype_3D", 30),
            ("pid_2_luid_0x0_0x123_phys_0_eng_0_engtype_3D", 40),
            ("pid_3_luid_0x0_0x123_phys_0_eng_1_engtype_Copy", 60)]));
        Assert.Null(ResourceCounters.GpuUsage([]));
    }
}
