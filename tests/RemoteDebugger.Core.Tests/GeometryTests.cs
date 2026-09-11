using RemoteDebugger.Core;
using Xunit;
namespace RemoteDebugger.Core.Tests;
public sealed class GeometryTests
{
    [Fact] public void MapsLetterboxedImageWithNegativeMonitorOrigin() { var g = new DesktopGeometry(-1920, 0, 1920, 1080, "a"); Assert.Equal((-960, 540), g.MapLetterbox(1000, 1000, 500, 500)); }
    [Fact] public void RejectsLetterboxPadding() { var g = new DesktopGeometry(0, 0, 1920, 1080, "a"); Assert.Null(g.MapLetterbox(1000, 1000, 500, 100)); }
    [Fact] public void ScaledCaptureUsesSourceDesktopCoordinates() { var g = new DesktopGeometry(100, 50, 3840, 2160, "a"); Assert.Equal((2020, 1130), g.MapLetterbox(1920, 1080, 960, 540)); }
    [Fact] public void RejectsZeroGeometry() => Assert.Null(new DesktopGeometry(0, 0, 0, 0, "a").MapLetterbox(100, 100, 50, 50));
    [Fact] public void ExcludesRightAndBottomEdges() => Assert.Null(new DesktopGeometry(0, 0, 100, 100, "a").MapLetterbox(100, 100, 100, 100));
}
