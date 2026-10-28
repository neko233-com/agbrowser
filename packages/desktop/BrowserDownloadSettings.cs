using System.Text.Json;

namespace AgBrowser.Desktop;

// A null request count follows the machine's logical processor count at startup.
internal sealed record BrowserDownloadSettings(int? ParallelRequests = null, int Version = 1)
{
    public const int MaximumRequests = 4096;
    private static string PathFor(string directory) => Path.Combine(directory, "download-settings.json");
    public static int AutomaticRequests => Math.Max(2, Environment.ProcessorCount * 2);
    public int EffectiveRequests => ParallelRequests ?? AutomaticRequests;

    public static bool TryLoad(string directory, out BrowserDownloadSettings settings, out string? error)
    {
        settings = new(); error = null;
        try
        {
            var loaded = JsonSerializer.Deserialize<BrowserDownloadSettings>(File.ReadAllText(PathFor(directory)));
            if (loaded is null || loaded.Version != 1 || loaded.ParallelRequests is < 1 or > MaximumRequests)
                throw new JsonException("下载设置格式或版本不受支持。");
            settings = loaded;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = "无法读取下载设置，原文件已保留。";
            return false;
        }
    }

    public string BrowserArguments() => EffectiveRequests == 1
        ? "--disable-features=ParallelDownloading"
        : $"--enable-features=ParallelDownloading:request_count/{EffectiveRequests}";

    public void Save(string directory)
    {
        if (Version != 1 || ParallelRequests is < 1 or > MaximumRequests)
            throw new ArgumentException($"并行请求数须为 1 到 {MaximumRequests}，或留空使用自动模式。");
        if (!TryLoad(directory, out _, out var error)) throw new IOException(error);
        Directory.CreateDirectory(directory);
        string path = PathFor(directory), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, path, true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public sealed partial class BrowserWindow
{
    private int runningParallelRequests = BrowserDownloadSettings.AutomaticRequests;

    private void ConfigureDownloads()
    {
        using var dialog = CreateDownloadSettingsDialog();
        if (dialog.ShowChromeDialog(this) == DialogResult.OK)
            ChromeMessage.Show(this, "设置已保存。完全退出并重新打开 agbrowser 后生效；当前下载继续由浏览器管理。", "下载设置");
    }

    private ChromeDialog CreateDownloadSettingsDialog()
    {
        bool readable = BrowserDownloadSettings.TryLoad(dataDir, out var current, out var error);
        var dialog = new ChromeDialog("下载设置", new Size(620, 405));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(26, 0, 26, 20) };
        foreach (int height in new[] { 34, 48, 62, 56, 75 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = new ChromeInput { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        var count = input.Field;
        count.AccessibleName = "每个下载的最大并行请求数";
        count.PlaceholderText = $"自动：CPU 逻辑处理器数 × 2（当前 {BrowserDownloadSettings.AutomaticRequests}）";
        count.Text = current.ParallelRequests?.ToString() ?? "";
        count.Enabled = readable;
        var hint = new Label { Text = $"留空为自动；输入 1 为单路，2–{BrowserDownloadSettings.MaximumRequests} 为并行目标。设置修改后需重启。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 99, 104) };
        var info = new Label { Text = "实际请求数由内核、文件大小和服务器决定；不支持分片时自动使用普通下载。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 99, 104) };
        var status = new Label { Text = error ?? $"本次启动使用的目标：{runningParallelRequests} 路。下载继续使用网页登录态和浏览器代理。", Dock = DockStyle.Fill, ForeColor = error is null ? Color.FromArgb(95, 99, 104) : Color.Firebrick };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new ChromeButton { Text = "保存", Enabled = readable }; var cancel = new ChromeButton { Text = "取消", DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => {
            try
            {
                string value = count.Text.Trim();
                int? requested = null;
                if (value.Length > 0)
                {
                    if (!int.TryParse(value, out int parsed) || parsed < 1 || parsed > BrowserDownloadSettings.MaximumRequests)
                        throw new ArgumentException($"请输入 1 到 {BrowserDownloadSettings.MaximumRequests} 的整数，或留空使用自动模式。");
                    requested = parsed;
                }
                new BrowserDownloadSettings(requested).Save(dataDir);
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { ChromeMessage.Show(dialog, ex.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(new Label { Text = "每个下载的并行请求数", Dock = DockStyle.Fill, ForeColor = BrowserPalette.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(input, 0, 1); layout.Controls.Add(hint, 0, 2); layout.Controls.Add(info, 0, 3); layout.Controls.Add(status, 0, 4); layout.Controls.Add(buttons, 0, 5);
        dialog.Body.Controls.Add(layout); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        return dialog;
    }
}
