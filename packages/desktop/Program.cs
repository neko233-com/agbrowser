namespace AgBrowser.Desktop;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == $"chrome-extension://{EdgeFavoritesBridge.ExtensionId}/")
            return EdgeFavoritesBridge.RunNativeHostAsync().GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        string dataDir = BrowserWindow.ResolveDataDir();
        using var instance = new SingleInstance(dataDir);
        if (!instance.IsOwner)
        {
            try { instance.ForwardAsync(args).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { ChromeMessage.Show($"无法向运行中的 agbrowser 打开链接：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
        try
        {
            using var window = new BrowserWindow(args);
            window.Shown += (_, _) => instance.Start(urls => {
                if (!window.IsDisposed && window.IsHandleCreated)
                    try { window.BeginInvoke(() => window.OpenExternal(urls)); } catch (InvalidOperationException) { }
            });
            Application.Run(window);
        }
        finally { BrowserNetworkSettings.ClearStaleDebugPort(dataDir); }
        return 0;
    }
}
