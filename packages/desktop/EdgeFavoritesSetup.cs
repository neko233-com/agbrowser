using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace AgBrowser.Desktop;

internal static class EdgeFavoritesSetup
{
    public static string Prepare()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agbrowser", "edge-favorites");
        Directory.CreateDirectory(dir);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var pair in new[] { ("agbrowser.edge.manifest.json", "manifest.json"), ("agbrowser.edge.service-worker.js", "service-worker.js") })
        {
            using var source = assembly.GetManifestResourceStream(pair.Item1) ?? throw new InvalidOperationException("收藏同步组件缺失");
            string file = Path.Combine(dir, pair.Item2), temporary = file + ".tmp";
            using (var destination = File.Create(temporary)) source.CopyTo(destination);
            File.Move(temporary, file, true);
        }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("找不到 agbrowser 程序路径");
        string hostManifest = Path.Combine(dir, "native-host.json");
        string content = JsonSerializer.Serialize(new { name = "com.agbrowser.edge_favorites", description = "agbrowser Edge 收藏同步", path = executable, type = "stdio", allowed_origins = new[] { $"chrome-extension://{EdgeFavoritesBridge.ExtensionId}/" } });
        File.WriteAllText(hostManifest + ".tmp", content);
        File.Move(hostManifest + ".tmp", hostManifest, true);
        using var registry = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Edge\NativeMessagingHosts\com.agbrowser.edge_favorites");
        registry.SetValue("", hostManifest, RegistryValueKind.String);
        return dir;
    }

    public static void ShowInstructions(IWin32Window owner)
    {
        try
        {
            string dir = Prepare();
            using var dialog = new ChromeDialog("连接 Edge 收藏", new Size(600, 320));
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 0, 26, 16), RowCount = 3, ColumnCount = 1 };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 96)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "1. 在 Edge 的扩展管理页打开开发人员模式。\r\n2. 选择“加载解压缩的扩展”，选择下方目录。\r\n3. 如果之前已安装，请在扩展管理页重新加载。\r\n4. 多个配置连接时，点击“已连接”选择收藏来源。" }, 0, 0);
            var pathInput = new ChromeInput { Dock = DockStyle.Fill }; pathInput.Field.Text = dir; pathInput.Field.ReadOnly = true; pathInput.Field.AccessibleName = "扩展目录"; content.Controls.Add(pathInput, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new ChromeButton { Text = "关闭", DialogResult = DialogResult.Cancel }; close.Click += (_, _) => dialog.Close();
            var open = new ChromeButton { Text = "打开 Edge 扩展页", Primary = true };
            open.Click += (_, _) => {
                string edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
                if (!File.Exists(edge)) edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe");
                if (File.Exists(edge)) Process.Start(new ProcessStartInfo(edge, "edge://extensions") { UseShellExecute = true });
                else ChromeMessage.Show(dialog, "请在 Edge 地址栏打开 edge://extensions。", "agbrowser");
            };
            buttons.Controls.Add(open); buttons.Controls.Add(close); content.Controls.Add(buttons, 0, 2); dialog.Body.Controls.Add(content); dialog.CancelButton = close; dialog.ShowChromeDialog(owner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { ChromeMessage.Show(owner, $"无法设置 Edge 收藏同步：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
