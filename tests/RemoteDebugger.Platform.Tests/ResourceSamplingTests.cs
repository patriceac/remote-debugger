using System.Diagnostics;
using System.Text.Json;
using RemoteDebugger;
using Xunit;

namespace RemoteDebugger.Platform.Tests;

public sealed class ResourceSamplingTests
{
    [Fact]
    public void CpuPercentUsesTheSameProcessInstanceAndRejectsInvalidBaselines()
    {
        DateTime start = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(25, ResourceSampling.CpuPercent(1_000, 1_500, start, start, 1_000, 2));
        Assert.Null(ResourceSampling.CpuPercent(1_000, 1_500, start, start.AddSeconds(1), 1_000, 2));
        Assert.Null(ResourceSampling.CpuPercent(1_500, 1_000, start, start, 1_000, 2));
        Assert.Null(ResourceSampling.CpuPercent(1_000, 1_500, start, start, 15_001, 2));
        Assert.Null(ResourceSampling.CpuPercent(1_000, 1_500, start, start, 0, 2));
    }

    [Fact]
    public async Task ProcessSamplingStartsWithoutCpuAndReusesItsCachedResult()
    {
        using var sampling = new ResourceSampling();

        object first = await sampling.ProcessesAsync(CancellationToken.None);
        object immediate = await sampling.ProcessesAsync(CancellationToken.None);

        Assert.Same(first, immediate);
        JsonElement result = JsonSerializer.SerializeToElement(first);
        Assert.Equal(0, result.GetProperty("intervalMs").GetDouble());
        Assert.All(result.GetProperty("processes").EnumerateArray(), process =>
            Assert.Equal(JsonValueKind.Null, process.GetProperty("cpuPercentTotalMachine").ValueKind));
    }

    [Fact]
    public async Task ProcessSamplingKeepsIdleNameWhenStartTimeIsUnavailable()
    {
        using var idle = Process.GetProcessById(0);
        using var sampling = new ResourceSampling();

        JsonElement result = JsonSerializer.SerializeToElement(
            await sampling.ProcessesAsync(CancellationToken.None));
        JsonElement idleResult = result.GetProperty("processes").EnumerateArray()
            .Single(process => process.GetProperty("pid").GetInt32() == idle.Id);

        Assert.Equal(idle.ProcessName, idleResult.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, idleResult.GetProperty("startUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, idleResult.GetProperty("unavailableReason").ValueKind);
    }

    [Fact]
    public async Task SystemSamplingStartsWithoutDeltasAndReusesItsCachedResult()
    {
        using var sampling = new ResourceSampling();

        object first = await sampling.SystemAsync(CancellationToken.None);
        object immediate = await sampling.SystemAsync(CancellationToken.None);

        Assert.Same(first, immediate);
        JsonElement result = JsonSerializer.SerializeToElement(first);
        Assert.Equal(0, result.GetProperty("intervalMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("cpuPercentTotalMachine").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("diskBusyPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("gpuPercent").ValueKind);
    }
}
