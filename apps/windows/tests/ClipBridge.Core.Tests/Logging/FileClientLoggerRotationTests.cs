using ClipBridge.Core.Logging;
using Xunit;

namespace ClipBridge.Core.Tests.Logging;

public class FileClientLoggerRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "clipbridge-rotation-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Rotation_RollsIntoBoundedFiles()
    {
        // 200 bytes per file, max 2 backups => total disk bounded at ~600 bytes.
        using (var logger = new FileClientLogger(_dir, "client.log", maxBytes: 200, maxFiles: 2))
        {
            for (var i = 0; i < 30; i++)
            {
                logger.Info($"line {i:D2} with some padding 0123456789");
            }
        }
        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        Assert.Contains("client.log", files);
        Assert.Contains("client.log.1", files);
        Assert.Contains("client.log.2", files);
        Assert.DoesNotContain("client.log.3", files); // bounded
        // newest content lives in client.log
        var current = File.ReadAllText(Path.Combine(_dir, "client.log"));
        Assert.Contains("line 29", current);
    }

    [Fact]
    public void Rotation_KeepsLoggerWorkingAcrossRolls()
    {
        using (var logger = new FileClientLogger(_dir, "client.log", maxBytes: 150, maxFiles: 2))
        {
            for (var i = 0; i < 50; i++)
            {
                logger.Info($"sequence {i}");
            }
        }
        // No exception → rotation is not fatal; subsequent lines still land.
        var current = File.ReadAllText(Path.Combine(_dir, "client.log"));
        Assert.Contains("sequence 49", current);
    }

    [Fact]
    public void Rotation_DisabledWithZeroMaxBytes()
    {
        using (var logger = new FileClientLogger(_dir, "client.log", maxBytes: 0, maxFiles: 3))
        {
            for (var i = 0; i < 20; i++)
            {
                logger.Info($"no-roll {i}");
            }
        }
        Assert.Single(Directory.GetFiles(_dir));
        var text = File.ReadAllText(Path.Combine(_dir, "client.log"));
        Assert.Contains("no-roll 19", text);
    }
}
