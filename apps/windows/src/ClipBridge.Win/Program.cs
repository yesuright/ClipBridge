using ClipBridge.Core.Config;
using ClipBridge.Core.Logging;

namespace ClipBridge.Win;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var config = ClientConfig.Load();

        if (!config.HasValidToken)
        {
            MessageBox.Show(
                "ClipBridge requires a device token.\n\n" +
                "Set CLIPBRIDGE_DEVICE_TOKEN (environment variable) or create\n" +
                $"{ClientConfig.DefaultConfigPath}\n" +
                "with a \"DeviceToken\" property matching the relay's CLIPBRIDGE_TOKEN.",
                "ClipBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipBridge");
        using var logger = new FileClientLogger(logDir);

        Application.Run(new TrayContext(config, logger));
    }
}
