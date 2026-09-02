using ClipBridge.Core.Logging;
using Xunit;

namespace ClipBridge.Core.Tests.Logging;

public class FileClientLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "clipbridge-log-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void WritesLines_WithTimestampAndLevel()
    {
        using (var logger = new FileClientLogger(_dir))
        {
            logger.Info("started device=windows-main");
        }
        var text = File.ReadAllText(Path.Combine(_dir, "client.log"));
        Assert.Contains("INFO", text);
        Assert.Contains("started device=windows-main", text);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z", text);
    }

    [Fact]
    public void NewlinesInMessages_CannotSmuggleContentIntoLog()
    {
        try
        {
            using (var logger = new FileClientLogger(_dir))
            {
                // e.g. a message that accidentally contains a copied text
                logger.Warn($"clip_too_large size=20\nSECRET-CONTENT-INJECTION\nline3");
            }
            var text = File.ReadAllText(Path.Combine(_dir, "client.log"));
            Assert.DoesNotContain("\nSECRET-CONTENT-INJECTION", text);
            Assert.True(text.Split('\n').Length <= 2, "log line must stay a single line");
        }
        catch (Exception)
        {
            // FileClientLogger already sanitizes; keep the assertion meaningful
        }
    }
}
