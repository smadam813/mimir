using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(8 * 1024 * 1024, "8.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2.0 TB")]
    public void FormatsToTheLargestUnitThatKeepsTheNumberReadable(long bytes, string expected)
        => ByteSize.Format(bytes).ShouldBe(expected);

    [Fact]
    public void ClampsAtTerabytes_RatherThanRunningOffTheUnitTable()
        => ByteSize.Format(9_000L * 1024 * 1024 * 1024 * 1024).ShouldEndWith(" TB");
}
