using Microsoft.Extensions.Logging;

namespace Mimir.Server.Tests;

public sealed class CapturedLogTests
{
    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void EveryLevelIsEnabled_SoAnIsEnabledGuardedBranchStillRuns(LogLevel level)
        => new CapturedLog().IsEnabled(level).ShouldBeTrue();

    [Fact]
    public void OnlyWarningsAreKept()
    {
        var log = new CapturedLog();

        foreach (var level in new[]
                 {
                     LogLevel.Trace, LogLevel.Debug, LogLevel.Information,
                     LogLevel.Warning, LogLevel.Error, LogLevel.Critical,
                 })
        {
            log.Log(level, default, level.ToString(), null, (state, _) => state);
        }

        log.Warnings.ShouldBe(["Warning"]);
    }
}
