using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace AgBrowser.Desktop;

internal sealed record BrowserNetworkSettings(string ProxyUrl = "", bool CaptureLoopback = false, bool AgentDebug = false)
{
    private static string PathFor(string directory) => Path.Combine(directory, "browser-network.json");
    public static string DebugPortFile(string directory) => Path.Combine(directory, "profile", "EBWebView", "DevToolsActivePort");
    public static string? ClearStaleDebugPort(string directory)
    {
        try { File.Delete(DebugPortFile(directory)); return null; }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        { return "无法清理上次调试端口文件，请检查数据目录权限：" + ex.Message; }
    }
    public static BrowserNetworkSettings Load(string directory)
    {
        try
        {
            var value = JsonSerializer.Deserialize<BrowserNetworkSettings>(File.ReadAllText(PathFor(directory))) ?? new();
            return value with { ProxyUrl = NormalizeProxy(value.ProxyUrl) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public static string NormalizeProxy(string? input)
    {
        input = input?.Trim() ?? "";
        if (input.Length == 0) return "";
        if (input.Length > 256 || input.Any(char.IsWhiteSpace) || !Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "socks5") ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is <= 0 or > 65535 || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入不含账号密码的代理地址，例如 http://127.0.0.1:8888。");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public string BrowserArguments()
    {
        var arguments = new List<string>();
        if (ProxyUrl.Length > 0)
        {
            arguments.Add("--proxy-server=" + NormalizeProxy(ProxyUrl));
            if (CaptureLoopback) arguments.Add("--proxy-bypass-list=<-loopback>");
        }
        if (AgentDebug) arguments.Add("--remote-debugging-port=0");
        return string.Join(' ', arguments);
    }
    public void Save(string directory)
    {
        var safe = this with { ProxyUrl = NormalizeProxy(ProxyUrl) };
        Directory.CreateDirectory(directory);
        string path = PathFor(directory), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(safe));
        File.Move(temp, path, true);
    }
}

public sealed partial class BrowserWindow
{
    private void ConfigureBrowserNetwork()
    {
        using var dialog = CreateBrowserNetworkDialog();
        if (dialog.ShowChromeDialog(this) == DialogResult.OK)
            ChromeMessage.Show(this, "设置已保存，完全退出并重新打开 agbrowser 后生效。", "代理和 Agent 调试");
    }

    private ChromeDialog CreateBrowserNetworkDialog()
    {
        var current = BrowserNetworkSettings.Load(dataDir);
        var dialog = new ChromeDialog("代理和 Agent 调试", new Size(620, 480));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(26, 0, 26, 20) };
        foreach (int height in new[] { 38, 25, 46, 36, 70, 76, 50 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = new ChromeInput { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
        var proxy = input.Field; proxy.Text = current.ProxyUrl; proxy.PlaceholderText = "例如 http://127.0.0.1:8888"; proxy.AccessibleName = "浏览器代理地址";
        var loopback = new ChromeToggle { Text = "也通过代理抓取 localhost / 127.0.0.1", AccessibleName = "通过代理抓取本机流量", Checked = current.CaptureLoopback, Anchor = AnchorStyles.Right };
        var debug = new ChromeToggle { Text = "开启本机 Agent / DevTools 调试端口", AccessibleName = "开启本机 Agent 调试端口", Checked = current.AgentDebug, Anchor = AnchorStyles.Right };
        static Control ToggleRow(string title, string description, ChromeToggle toggle)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var label = new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = BrowserPalette.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) };
            var hint = new Label { Text = description, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(112, 117, 122) };
            label.Click += (_, _) => toggle.Checked = !toggle.Checked; hint.Click += (_, _) => toggle.Checked = !toggle.Checked;
            row.Controls.Add(label, 0, 0); row.Controls.Add(hint, 0, 1); row.Controls.Add(toggle, 1, 0); row.SetRowSpan(toggle, 2);
            return row;
        }
        string debugStatus = debugPortWarning is { } warning ? warning : runningAgentDebug
            ? "当前调试端口信息：" + BrowserNetworkSettings.DebugPortFile(dataDir)
            : "当前状态：Agent 调试已关闭";
        var port = new Label { Dock = DockStyle.Fill, Text = debugStatus, AutoEllipsis = true, ForeColor = Color.FromArgb(95, 99, 104) };
        tooltip.SetToolTip(port, debugStatus);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new ChromeButton { Text = "保存" }; var cancel = new ChromeButton { Text = "取消", DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => {
            try { new BrowserNetworkSettings(proxy.Text, loopback.Checked, debug.Checked).Save(dataDir); dialog.DialogResult = DialogResult.OK; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { ChromeMessage.Show(dialog, ex.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(new Label { Text = "Charles 等抓包工具可使用系统代理或填写地址。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(95, 99, 104) }, 0, 0);
        layout.Controls.Add(new Label { Text = "代理地址", Dock = DockStyle.Fill, ForeColor = BrowserPalette.Ink, Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold) }, 0, 1);
        layout.Controls.Add(input, 0, 2);
        layout.Controls.Add(new Label { Text = "留空使用系统代理；不要填写账号密码。HTTPS 抓包需信任代理证书。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(112, 117, 122) }, 0, 3);
        layout.Controls.Add(ToggleRow("代理本机流量", "包括 localhost 和 127.0.0.1", loopback), 0, 4);
        layout.Controls.Add(ToggleRow("允许 Agent 调试", "调试端口可控制浏览器，请仅在需要时开启。", debug), 0, 5);
        layout.Controls.Add(port, 0, 6); layout.Controls.Add(buttons, 0, 7);
        dialog.Body.Controls.Add(layout); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        return dialog;
    }

    private sealed record AiEntry(string Name, string Url);
    private static readonly AiEntry[] AiEntries = [
        new("ChatGPT · 网页", "https://chatgpt.com/"), new("ChatGPT · 桌面下载", "https://chatgpt.com/download/"),
        new("Claude · 网页", "https://claude.ai/"), new("Claude · 桌面下载", "https://claude.com/download"),
        new("Gemini", "https://gemini.google.com/"), new("Microsoft Copilot", "https://copilot.microsoft.com/"),
        new("DeepSeek", "https://chat.deepseek.com/"), new("通义千问", "https://chat.qwen.ai/"),
        new("Perplexity", "https://www.perplexity.ai/")
    ];
    private string AiCatalogPath => Path.Combine(dataDir, "ai-entries.json");
    private AiEntry[] ReadAiEntries()
    {
        if (!File.Exists(AiCatalogPath)) return [];
        return JsonSerializer.Deserialize<AiEntry[]>(File.ReadAllText(AiCatalogPath)) ?? throw new JsonException("AI 入口文件格式无效。");
    }
    private void WriteAiEntries(AiEntry[] entries)
    {
        Directory.CreateDirectory(dataDir); string temp = AiCatalogPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries)); File.Move(temp, AiCatalogPath, true);
    }
    private void ShowAiMenu()
    {
        var (menu, search) = CreateAiMenu();
        DisposeMenuAfterClose(menu); menu.Show(aiButton, new Point(0, aiButton.Height)); search.Focus();
    }

    private (ChromeContextMenu Menu, TextBox Search) CreateAiMenu()
    {
        AiEntry[] custom;
        try { custom = ReadAiEntries().Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name) && Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https") && uri.UserInfo.Length == 0).Take(100).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { ChromeMessage.Show(this, "无法读取自定义 AI 入口，原文件已保留：" + ex.Message, "AI 入口"); custom = []; }
        var menu = new ChromeContextMenu { AutoClose = true, ShowImageMargin = false };
        var searchInput = new ChromeInput { Size = new Size(300, 40) }; var search = searchInput.Field;
        search.AccessibleName = "搜索 AI 入口"; search.PlaceholderText = "搜索 AI 网站或桌面应用";
        var searchHost = new ToolStripControlHost(searchInput) { AutoSize = false, Size = searchInput.Size, Margin = new Padding(4) };
        menu.Items.Add(searchHost); menu.Items.Add(new ToolStripSeparator());
        var catalog = AiEntries.Concat(custom).ToArray();
        var entries = catalog.Select(item => {
            var entry = new ToolStripMenuItem(item.Name) { ToolTipText = item.Url };
            entry.Click += async (_, _) => await AddTab(item.Url); menu.Items.Add(entry); return entry;
        }).ToArray();
        search.TextChanged += (_, _) => { for (int i = 0; i < entries.Length; i++) entries[i].Visible = catalog[i].Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || catalog[i].Url.Contains(search.Text, StringComparison.OrdinalIgnoreCase); };
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && entries.FirstOrDefault(x => x.Visible) is { } first) { first.PerformClick(); menu.Close(); e.SuppressKeyPress = true; } };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("添加自定义入口…", null, (_, _) => AddAiEntry());
        if (custom.Length > 0)
        {
            var remove = new ToolStripMenuItem("移除自定义入口");
            ChromeMenuStyle.Apply(remove.DropDown);
            foreach (var item in custom) remove.DropDownItems.Add(item.Name, null, (_, _) => {
                if (ChromeMessage.Show(this, $"移除“{item.Name}”？", "AI 入口", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                try { WriteAiEntries(ReadAiEntries().Where(x => x != item).ToArray()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { ChromeMessage.Show(this, ex.Message, "AI 入口"); }
            });
            menu.Items.Add(remove);
        }
        return (menu, search);
    }

    private void AddAiEntry()
    {
        using var dialog = new ChromeDialog("添加 AI 入口", new Size(520, 255));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(26, 0, 26, 16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var nameInput = new ChromeInput { Dock = DockStyle.Fill }; var name = nameInput.Field; name.AccessibleName = "入口名称";
        var urlInput = new ChromeInput { Dock = DockStyle.Fill }; var url = urlInput.Field; url.PlaceholderText = "https://…"; url.AccessibleName = "入口网址";
        var save = new ChromeButton { Text = "保存", Anchor = AnchorStyles.Right };
        save.Click += (_, _) => {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim().Length > 80 || !Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0) { ChromeMessage.Show(dialog, "请输入名称和不含账号密码的 HTTP 或 HTTPS 网址。", "AI 入口"); return; }
            try { var entries = ReadAiEntries(); if (entries.Length >= 100) throw new ArgumentException("最多保存 100 个自定义入口。"); WriteAiEntries(entries.Append(new AiEntry(name.Text.Trim(), uri.AbsoluteUri)).ToArray()); dialog.DialogResult = DialogResult.OK; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { ChromeMessage.Show(dialog, ex.Message, "AI 入口"); }
        };
        layout.Controls.Add(new Label { Text = "名称", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); layout.Controls.Add(nameInput, 1, 0); layout.Controls.Add(new Label { Text = "网址", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1); layout.Controls.Add(urlInput, 1, 1); layout.Controls.Add(save, 1, 2); dialog.Body.Controls.Add(layout); dialog.AcceptButton = save; dialog.ShowChromeDialog(this);
    }

    private async Task ShowExtensionCenter()
    {
        if (await EnsureActiveCore() is not { } core) return;
        var profile = core.Profile;
        using var dialog = new ChromeDialog("插件中心", new Size(700, 510), resizable: true) { MinimumSize = new Size(620, 430) };
        using var stop = new CancellationTokenSource();
        CancellationToken cancellation = stop.Token;
        dialog.FormClosed += (_, _) => stop.Cancel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(20, 0, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "可安装 Chrome 商店公开插件或本机已解压插件。商店插件需手动更新；部分插件工具栏和 API 可能不兼容。" }, 0, 0);
        var list = new ListBox { Dock = DockStyle.Fill, DisplayMember = nameof(ExtensionRow.Display), AccessibleName = "已安装插件" };
        layout.Controls.Add(list, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var addStore = new ChromeButton { Text = "从 Chrome 商店安装", Primary = true, AccessibleName = "从 Chrome 商店安装插件" };
        var add = new ChromeButton { Text = "添加已解压插件" }; var toggle = new ChromeButton { Text = "启用 / 停用" }; var remove = new ChromeButton { Text = "移除" };
        actions.Controls.Add(addStore); actions.Controls.Add(add); actions.Controls.Add(toggle); actions.Controls.Add(remove); layout.Controls.Add(actions, 0, 2);
        var stores = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var chrome = new ChromeButton { Text = "Chrome 网上应用店" }; var edge = new ChromeButton { Text = "Edge 扩展商店" }; stores.Controls.Add(chrome); stores.Controls.Add(edge); layout.Controls.Add(stores, 0, 3);
        var status = new Label { Dock = DockStyle.Fill, ForeColor = BrowserPalette.Ink, AccessibleName = "插件安装状态" }; layout.Controls.Add(status, 0, 4); dialog.Body.Controls.Add(layout);
        async Task Refresh()
        {
            try { var items = await profile.GetBrowserExtensionsAsync(); if (dialog.IsDisposed) return; list.Items.Clear(); foreach (var item in items) list.Items.Add(new ExtensionRow(item)); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException) { if (!dialog.IsDisposed) ChromeMessage.Show(dialog, "无法读取插件：" + ex.Message, "插件中心"); }
        }
        add.Click += async (_, _) => {
            using var picker = new FolderBrowserDialog { Description = "选择包含 manifest.json 的插件目录", UseDescriptionForTitle = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            if (!File.Exists(Path.Combine(picker.SelectedPath, "manifest.json"))) { ChromeMessage.Show(dialog, "所选目录不包含 manifest.json。", "插件中心"); return; }
            if (ChromeMessage.Show(dialog, "插件可能读取和修改网页数据。确认从此目录安装？请保留该目录，移动或修改文件会导致插件失效。", "安装插件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { await profile.AddBrowserExtensionAsync(picker.SelectedPath); await Refresh(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException) { ChromeMessage.Show(dialog, "安装失败：" + ex.Message, "插件中心"); }
        };
        addStore.Click += async (_, _) => {
            string initial = "";
            try { if (active?.Url is { } current) { _ = ChromeStoreExtensions.ParseId(current); initial = current; } } catch (ArgumentException) { }
            string? input = PromptChromeStore(dialog, initial); if (input is null) return;
            addStore.Enabled = false; status.Text = "正在下载并验证插件…";
            try
            {
                string id = ChromeStoreExtensions.ParseId(input);
                string version = Regex.Match(core.Settings.UserAgent, @"Chrome/(\d+(?:\.\d+){0,3})").Groups[1].Value;
                if (version.Length == 0) throw new InvalidOperationException("无法确定当前 Chromium 版本。");
                var package = await ChromeStoreExtensions.DownloadAsync(id, version, BrowserNetworkSettings.Load(dataDir).ProxyUrl, cancellation);
                if (dialog.IsDisposed || cancellation.IsCancellationRequested) return;
                var result = await ChromeStoreInstallation.InstallVerifiedAsync(package, dataDir, profile,
                    (verified, replacing) => !dialog.IsDisposed && !cancellation.IsCancellationRequested && ConfirmChromeStorePermissions(dialog, verified, replacing),
                    text => { if (!dialog.IsDisposed) status.Text = text; }, cancellation);
                if (dialog.IsDisposed) return;
                if (!result.Installed) { status.Text = "已取消安装。"; return; }
                await Refresh();
                if (!dialog.IsDisposed) status.Text = $"已安装 {result.Name}。";
            }
            catch (OperationCanceledException) { if (!dialog.IsDisposed) status.Text = "安装已取消或网络超时。"; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
            {
                if (!dialog.IsDisposed) { status.Text = "安装失败。"; string detail = ex is ChromeStoreInstallException { InnerException: { } cause } ? ex.Message + "\r\n" + cause.Message : ex.Message; ChromeMessage.Show(dialog, "无法安装插件：" + detail, "插件中心", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
            finally { if (!dialog.IsDisposed) addStore.Enabled = true; }
        };
        toggle.Click += async (_, _) => { if (list.SelectedItem is not ExtensionRow row) return; try { await row.Extension.EnableAsync(!row.Extension.IsEnabled); await Refresh(); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { ChromeMessage.Show(dialog, ex.Message, "插件中心"); } };
        remove.Click += async (_, _) => { if (list.SelectedItem is not ExtensionRow row || ChromeMessage.Show(dialog, "移除此插件？商店插件的本机解包文件会保留。", "插件中心", MessageBoxButtons.YesNo) != DialogResult.Yes) return; try { await row.Extension.RemoveAsync(); await Refresh(); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { ChromeMessage.Show(dialog, ex.Message, "插件中心"); } };
        chrome.Click += async (_, _) => { dialog.Close(); await AddTab("https://chromewebstore.google.com/"); };
        edge.Click += async (_, _) => { dialog.Close(); await AddTab("https://microsoftedge.microsoft.com/addons/"); };
        dialog.Shown += async (_, _) => await Refresh(); dialog.ShowChromeDialog(this);
    }
    private static string? PromptChromeStore(IWin32Window owner, string initial)
    {
        using var dialog = new ChromeDialog("从 Chrome 商店安装", new Size(560, 225));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 0, 26, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var inputBox = new ChromeInput { Dock = DockStyle.Fill }; var input = inputBox.Field; input.Text = initial; input.PlaceholderText = "Chrome 商店插件网址或 32 位插件 ID"; input.AccessibleName = "Chrome 商店插件网址或 ID";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var next = new ChromeButton { Text = "下一步", DialogResult = DialogResult.OK }; var cancel = new ChromeButton { Text = "取消", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(next); buttons.Controls.Add(cancel);
        layout.Controls.Add(new Label { Text = "输入 Chrome 网上应用店的插件网址或 ID", Dock = DockStyle.Fill }, 0, 0); layout.Controls.Add(inputBox, 0, 1); layout.Controls.Add(buttons, 0, 2);
        dialog.Body.Controls.Add(layout); dialog.AcceptButton = next; dialog.CancelButton = cancel;
        return dialog.ShowChromeDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
    private static bool ConfirmChromeStorePermissions(IWin32Window owner, ChromeStorePackage package, bool replacing)
    {
        using var dialog = new ChromeDialog("确认安装插件", new Size(600, 490), resizable: true) { MinimumSize = new Size(500, 400) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 0, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var title = new Label { Text = $"{package.Name}  ·  {package.Version}", Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold) };
        var id = new Label { Text = "商店 ID：" + package.Id, Dock = DockStyle.Fill, AutoEllipsis = true };
        string required = package.Permissions.Length == 0 ? "（未声明）" : string.Join("\r\n", package.Permissions.Select(value => "• " + value));
        string optional = package.OptionalPermissions.Length == 0 ? "（未声明）" : string.Join("\r\n", package.OptionalPermissions.Select(value => "• " + value));
        var permissions = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "插件声明的权限",
            Text = (replacing ? "此插件已安装，本次操作将替换现有版本。\r\n\r\n" : "") + "请确认信任该插件。WebView2 不提供通用插件工具栏，部分功能可能不可用；商店插件需手动更新。\r\n\r\n必要权限\r\n" + required + "\r\n\r\n可选权限（插件可能在使用时请求）\r\n" + optional };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var install = new ChromeButton { Text = "安装", DialogResult = DialogResult.OK }; var cancel = new ChromeButton { Text = "取消", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(install); buttons.Controls.Add(cancel);
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(id, 0, 1); layout.Controls.Add(permissions, 0, 2); layout.Controls.Add(buttons, 0, 3);
        dialog.Body.Controls.Add(layout); dialog.AcceptButton = install; dialog.CancelButton = cancel;
        return dialog.ShowChromeDialog(owner) == DialogResult.OK;
    }
    private sealed record ExtensionRow(CoreWebView2BrowserExtension Extension) { public string Display => $"{(Extension.IsEnabled ? "已启用" : "已停用")}  {Extension.Name}"; }
}
