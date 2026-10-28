using System.Text.Json;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AgBrowser.Desktop;

public sealed partial class BrowserWindow : Form
{
    internal static string ResolveDataDir() => Environment.GetEnvironmentVariable("AGB_DESKTOP_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agbrowser", "desktop");
    private readonly string dataDir = ResolveDataDir();
    private readonly List<string> pendingExternalUrls = [];
    private readonly FlowLayoutPanel rail = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, AutoScrollMargin = new Size(0, 8), BackColor = BrowserPalette.Chrome, Padding = new Padding(3, 8, 3, 4) };
    private readonly Panel content = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly NewTabPage newTabPage = new();
    private readonly Panel recovery = new() { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
    private readonly Label recoveryMessage = new() { AutoSize = true, MaximumSize = new Size(520, 0), Font = new Font("Microsoft YaHei UI", 12), ForeColor = BrowserPalette.Ink, Margin = new Padding(0, 0, 0, 16) };
    private readonly Button recoveryButton = new() { Text = "重新加载", AutoSize = true, Padding = new Padding(14, 4, 14, 4), AccessibleName = "重新加载出错的页面" };
    private readonly TextBox address = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = BrowserPalette.Ink, Font = new Font("Segoe UI", 10.5f) };
    private readonly ListBox addressSuggestions = new() { BorderStyle = BorderStyle.None, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9.5f), AccessibleName = "地址建议" };
    private readonly AddressSuggestionPopup suggestionPopup = new() { AutoClose = false, Padding = new Padding(1, 2, 1, 2), BackColor = Color.White };
    private readonly System.Windows.Forms.Timer suggestionFocusTimer = new() { Interval = 150 };
    private readonly Dictionary<string, Image> suggestionIcons = new(StringComparer.Ordinal);
    private readonly Font suggestionBoldFont = new("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    private int suggestionHover = -1;
    private ToolStripControlHost? suggestionHost;
    private AddressPanel? addressPanel;
    private bool showingSuggestions;
    private CancellationTokenSource? suggestionSearch;
    private int suggestionGeneration;
    private readonly Button back = Nav("‹"), forward = Nav("›"), reload = Nav("↻"), home = Nav("⌂"), downloads = Nav("↓"), menuButton = Nav("⋯");
    private readonly Button updateButton = Nav("更新");
    private readonly Button aiButton = Nav("✦ AI"), extensionButton = Nav("插件");
    private readonly LoadingIndicator loadingIndicator = new();
    private readonly AdBlockSettings adBlock;
    private readonly AddressHistory addressHistory;
    private readonly UpdateService updateService;
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private UpdatePackage? availableUpdate;
    private bool updateBusy;
    private bool downloadCloseConfirmedForUpdate;
    private readonly ToolTip tooltip = new();
    private readonly List<BrowserTab> tabs = [];
    private readonly HashSet<BrowserTab> retainedDownloadTabs = [];
    private readonly List<ClosedTab> recentlyClosed = [];
    private readonly EdgeFavoritesReader edgeFavorites = new();
    private readonly EdgeFavoritesBridge edgeBridge = new();
    private readonly Button favoriteAdd = Nav("☆");
    private readonly Button favoriteConnect = Nav("同步");
    private readonly Button favoriteManage = Nav("管理");
    private FavoriteSnapshot? bridgeSnapshot;
    private FavoriteSnapshot? displayedFavorites;
    private Dictionary<string, byte[]> favoriteIcons = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> favoritePageIndex = new(StringComparer.Ordinal);
    private readonly FavoritePageIconCache favoritePageIconCache;
    private CancellationTokenSource? favoriteIconLoad;
    private readonly System.Windows.Forms.Timer favoriteIconSaveTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer favoritesTimer = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer railCollapseTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer backgroundMemoryTimer = new() { Interval = 5_000 };
    private static readonly TimeSpan BackgroundMemoryDelay = TimeSpan.FromMinutes(2);
    private const int BackgroundMemoryBatchSize = 8;
    private readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer historyTimer = new() { Interval = 2000 };
    private readonly ToolStrip favoritesBar = new() { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, CanOverflow = true, BackColor = BrowserPalette.Toolbar, Padding = new Padding(4, 0, 4, 0), RenderMode = ToolStripRenderMode.System, AccessibleName = "Edge 收藏栏" };
    private readonly Label sessionWarning = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 10, 0), BackColor = Color.FromArgb(255, 245, 220), ForeColor = Color.FromArgb(115, 70, 0), Visible = false, AccessibleName = "会话保存状态" };
    private readonly RowStyle sessionWarningRow = new(SizeType.Absolute, 0);
    private bool refreshingFavorites;
    private bool railExpanded;
    private Panel? sidePanel;
    private Action? updateRailWidth;
    private int railMetricsBatchDepth;
    private bool railMetricsPending;
    private BrowserTab? draggingTab;
    private RailTabButton? dragMarker;
    private readonly string tabDragFormat = "agbrowser-tab-" + Guid.NewGuid().ToString("N");
    private BrowserTab? active;
    private CoreWebView2Environment? environment;
    private bool browserProcessFailed;
    private bool browserResourcesReleased;
    private bool recoveryInProgress;
    private bool recoveryUnavailable;
    private bool memoryWindowMinimized;
    private int backgroundMemoryCursor;
    private bool runningAgentDebug;
    private string? debugPortWarning;
    private bool submittingAddress;
    private bool sessionRestored;
    private bool sessionReadable = true;
    private readonly string[] initialUrls;
    private static readonly string[] BlockedHosts = ["doubleclick.net", "googlesyndication.com", "googleadservices.com", "adnxs.com", "adsrvr.org", "amazon-adsystem.com", "criteo.com", "criteo.net", "pubmatic.com", "rubiconproject.com", "openx.net", "taboola.com", "outbrain.com", "2mdn.net", "googletagservices.com", "adservice.google.com", "adservice.googleusercontent.com", "yieldmo.com", "smartadserver.com", "casalemedia.com", "33across.com", "adroll.com", "zedo.com", "sharethrough.com"];

    public BrowserWindow(string[] args)
    {
        adBlock = new AdBlockSettings(dataDir);
        addressHistory = new AddressHistory(dataDir);
        favoritePageIconCache = new FavoritePageIconCache(dataDir);
        updateService = new UpdateService(dataDir);
        initialUrls = args.Where(x => !x.StartsWith('-')).ToArray();
        Text = "agbrowser"; Width = 1300; Height = 850; MinimumSize = new Size(600, 400); StartPosition = FormStartPosition.CenterScreen; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true;
        KeyPreview = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(sessionWarningRow); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // The expanded rail overlays the page instead of changing the page's
        // width each time the pointer enters or leaves a tab.
        var side = new Panel { Margin = Padding.Empty, BackColor = rail.BackColor }; sidePanel = side;
        var plus = Nav("+"); plus.Dock = DockStyle.Bottom; plus.Height = 42; plus.Click += async (_, _) => await AddTab("about:blank", focusAddress: true);
        plus.MouseEnter += (_, _) => railCollapseTimer.Stop();
        plus.MouseLeave += (_, _) => StartRailCollapse();
        side.Controls.Add(rail); side.Controls.Add(plus);
        void UpdateRailWidth()
        {
            if (railMetricsBatchDepth > 0) { railMetricsPending = true; return; }
            railMetricsPending = false;
            int contentHeight = rail.Padding.Vertical + rail.AutoScrollMargin.Height + rail.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
            if (rail.AutoScrollMinSize.Height != contentHeight) rail.AutoScrollMinSize = new Size(0, contentHeight);
            int scrollbarWidth = contentHeight > rail.Height ? SystemInformation.VerticalScrollBarWidth : 0;
            int collapsedWidth = LogicalToDeviceUnits(52) + scrollbarWidth;
            int visibleWidth = LogicalToDeviceUnits(railExpanded ? 240 : 52) + scrollbarWidth;
            if (layout.ColumnStyles[0].Width != collapsedWidth) layout.ColumnStyles[0].Width = collapsedWidth;
            if (side.Width != visibleWidth) side.Width = visibleWidth;
        }
        updateRailWidth = UpdateRailWidth;
        DpiChanged += (_, _) => DispatchUi(() => { foreach (var button in rail.Controls.OfType<RailTabButton>()) button.RefreshDpiMetrics(); UpdateRailWidth(); if (suggestionPopup.Visible) PositionAddressSuggestions(); });
        rail.ControlAdded += (_, _) => UpdateRailWidth();
        rail.ControlRemoved += (_, _) => UpdateRailWidth();
        rail.SizeChanged += (_, _) => UpdateRailWidth();
        side.MouseLeave += (_, _) => StartRailCollapse();
        rail.MouseLeave += (_, _) => StartRailCollapse();
        side.MouseEnter += (_, _) => railCollapseTimer.Stop();
        rail.MouseEnter += (_, _) => railCollapseTimer.Stop();
        railCollapseTimer.Tick += (_, _) => {
            railCollapseTimer.Stop();
            if (sidePanel is { IsDisposed: false } panel && !panel.ClientRectangle.Contains(panel.PointToClient(Cursor.Position))) SetRailExpanded(false);
        };
        rail.AllowDrop = true;
        rail.DragEnter += TabDragOver;
        rail.DragOver += TabDragOver;
        rail.DragDrop += (_, e) => {
            if (IsTabDrag(e) && draggingTab is { } source)
            {
                int y = rail.PointToClient(new Point(e.X, e.Y)).Y;
                int target = tabs.FindIndex(tab => y < tab.Button.Top + tab.Button.Height / 2);
                if (target < 0) target = tabs.Count;
                if (tabs.IndexOf(source) < target) target--;
                MoveTab(source, target);
            }
            ClearTabDragMarker();
        };
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 2, Padding = new Padding(4, 6, 8, 3), Margin = Padding.Empty, BackColor = BrowserPalette.Toolbar };
        // An implicit AutoSize row follows AddressPanel's default preferred
        // height (100px), centering every icon below the clipped toolbar.
        toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 3));
        for (int i = 0; i < 4; i++) toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        toolbar.Controls.Add(back, 0, 0); toolbar.Controls.Add(forward, 1, 0); toolbar.Controls.Add(reload, 2, 0); toolbar.Controls.Add(home, 3, 0);
        addressPanel = new AddressPanel(); addressPanel.Controls.Add(address); toolbar.Controls.Add(addressPanel, 4, 0); toolbar.Controls.Add(loadingIndicator, 4, 1); toolbar.Controls.Add(aiButton, 5, 0); toolbar.Controls.Add(extensionButton, 6, 0); toolbar.Controls.Add(downloads, 7, 0); toolbar.Controls.Add(updateButton, 8, 0); toolbar.Controls.Add(menuButton, 9, 0);
        aiButton.Font = new Font("Segoe UI", 9, FontStyle.Bold); aiButton.ForeColor = BrowserPalette.Accent; aiButton.AccessibleName = "AI 入口";
        extensionButton.Font = new Font("Microsoft YaHei UI", 8.5f); extensionButton.AccessibleName = "插件中心";
        tooltip.SetToolTip(loadingIndicator, "页面加载中"); tooltip.SetToolTip(aiButton, "搜索 AI 网站和桌面应用"); tooltip.SetToolTip(extensionButton, "插件中心");
        aiButton.Click += (_, _) => ShowAiMenu(); extensionButton.Click += async (_, _) => await ShowExtensionCenter();
        updateButton.Font = new Font("Microsoft YaHei UI", 8.5f); updateButton.AccessibleName = "检查更新";
        tooltip.SetToolTip(updateButton, "检查 GitHub 更新");
        updateButton.Click += async (_, _) => { if (availableUpdate is { } package) await DownloadAndConfirmUpdate(package); else await CheckForUpdates(true); };
        addressPanel.SizeChanged += (_, _) => { if (suggestionPopup.Visible) PositionAddressSuggestions(); };
        addressPanel.LocationChanged += (_, _) => { if (suggestionPopup.Visible) PositionAddressSuggestions(); };
        LocationChanged += (_, _) => { if (suggestionPopup.Visible) PositionAddressSuggestions(); };
        ResizeBegin += (_, _) => DismissAddressSuggestions();
        Resize += (_, _) => {
            bool minimized = WindowState == FormWindowState.Minimized;
            if (minimized) DismissAddressSuggestions();
            if (minimized == memoryWindowMinimized) return;
            memoryWindowMinimized = minimized;
            if (active is { } current)
            {
                if (minimized) current.InactiveSince = Stopwatch.GetTimestamp();
                else if (current.LowMemoryApplied) SetTabMemoryTarget(current, low: false);
            }
        };
        Deactivate += (_, _) => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke(DismissSuggestionsIfBackground); } catch (InvalidOperationException) { } };
        suggestionPopup.Opened += (_, _) => suggestionFocusTimer.Start();
        suggestionPopup.Closed += (_, _) => suggestionFocusTimer.Stop();
        suggestionFocusTimer.Tick += (_, _) => DismissSuggestionsIfBackground();
        suggestionHost = new ToolStripControlHost(addressSuggestions) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
        suggestionPopup.Items.Add(suggestionHost);
        addressSuggestions.DrawItem += DrawAddressSuggestion;
        addressSuggestions.MouseMove += (_, e) => { int index = addressSuggestions.IndexFromPoint(e.Location); if (suggestionHover == index) return; int previous = suggestionHover; suggestionHover = index; if (previous >= 0 && previous < addressSuggestions.Items.Count) addressSuggestions.Invalidate(addressSuggestions.GetItemRectangle(previous)); if (index >= 0) addressSuggestions.Invalidate(addressSuggestions.GetItemRectangle(index)); };
        addressSuggestions.MouseLeave += (_, _) => { if (suggestionHover >= 0 && suggestionHover < addressSuggestions.Items.Count) { int previous = suggestionHover; suggestionHover = -1; addressSuggestions.Invalidate(addressSuggestions.GetItemRectangle(previous)); } };
        addressSuggestions.KeyDown += OnShortcut;
        addressSuggestions.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left && addressSuggestions.IndexFromPoint(e.Location) >= 0) UseAddressSuggestion(); };
        addressSuggestions.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Enter) { UseAddressSuggestion(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { DismissAddressSuggestions(); address.Focus(); e.SuppressKeyPress = true; }
        };
        addressSuggestions.Leave += (_, _) => CloseSuggestionsWhenUnfocused();
        back.AccessibleName = "后退"; forward.AccessibleName = "前进"; reload.AccessibleName = "刷新"; home.AccessibleName = "主页"; downloads.AccessibleName = "下载"; menuButton.AccessibleName = "浏览器菜单"; plus.AccessibleName = "新建标签页"; address.AccessibleName = "地址和搜索";
        tooltip.SetToolTip(back, "后退"); tooltip.SetToolTip(forward, "前进"); tooltip.SetToolTip(reload, "刷新"); tooltip.SetToolTip(home, "主页"); tooltip.SetToolTip(downloads, "下载 (Ctrl+J)"); tooltip.SetToolTip(menuButton, "浏览器菜单"); tooltip.SetToolTip(plus, "新建标签页 (Ctrl+T)");
        layout.Controls.Add(toolbar, 1, 0);
        var favoritesRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = BrowserPalette.Toolbar, Margin = Padding.Empty };
        favoritesRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        favoritesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); favoritesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); favoritesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34)); favoritesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        favoriteAdd.AccessibleName = "添加到 Edge 收藏栏"; favoriteAdd.Font = new Font("Segoe UI", 13); favoriteAdd.Enabled = false;
        favoriteConnect.AccessibleName = "连接 Edge 收藏同步"; favoriteConnect.Font = new Font("Microsoft YaHei UI", 8);
        favoriteManage.AccessibleName = "搜索和管理收藏"; favoriteManage.Font = new Font("Microsoft YaHei UI", 8); favoriteManage.Click += (_, _) => ShowFavoritesManager();
        tooltip.SetToolTip(favoriteAdd, "添加到 Edge 收藏栏"); tooltip.SetToolTip(favoriteConnect, "连接 Edge 收藏同步");
        favoriteAdd.Click += async (_, _) => await AddCurrentFavorite();
        favoritesBar.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) { var menu = new ChromeContextMenu(); menu.Items.Add("搜索和管理收藏", null, (_, _) => ShowFavoritesManager()); if (bridgeSnapshot is { BarId.Length: > 0 } snapshot) menu.Items.Add("新建文件夹", null, async (_, _) => await CreateFolder(snapshot.BarId, snapshot.ConnectionGeneration)); DisposeMenuAfterClose(menu); menu.Show(favoritesBar, e.Location); } };
        favoriteConnect.Click += (_, _) => ShowEdgeConnections();
        favoritesRow.Controls.Add(favoritesBar, 0, 0); favoritesRow.Controls.Add(favoriteManage, 1, 0); favoritesRow.Controls.Add(favoriteAdd, 2, 0); favoritesRow.Controls.Add(favoriteConnect, 3, 0);
        var recoveryContent = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Anchor = AnchorStyles.None };
        recoveryContent.Controls.Add(recoveryMessage); recoveryContent.Controls.Add(recoveryButton);
        var recoveryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
        recoveryLayout.Controls.Add(recoveryContent, 0, 0); recovery.Controls.Add(recoveryLayout); content.Controls.Add(recovery); content.Controls.Add(newTabPage);
        newTabPage.SearchRequested += (_, _) => { if (address.Enabled && address.Focus()) address.SelectAll(); };
        recoveryButton.Click += async (_, _) => { if (browserProcessFailed) await RecoverBrowser(); else RetryFailedPage(); };
        layout.Controls.Add(favoritesRow, 1, 1); layout.Controls.Add(sessionWarning, 1, 2); layout.Controls.Add(content, 1, 3); Controls.Add(layout);
        Controls.Add(side); side.BringToFront();
        void UpdateRailHeight() { if (side.Height != ClientSize.Height) side.Height = ClientSize.Height; }
        ClientSizeChanged += (_, _) => UpdateRailHeight();
        UpdateRailHeight(); UpdateRailWidth();
        favoritesBar.Items.Add(new ToolStripLabel("Edge 收藏栏") { ForeColor = Color.Gray });
        edgeBridge.SnapshotReceived += snapshot => DispatchUi(() => { if (!edgeBridge.IsCurrentConnection(snapshot.ConnectionGeneration)) return; bridgeSnapshot = snapshot; UpdateFavoriteAvailability(); UpdateEdgeConnectionButton(); ApplyFavorites(snapshot); });
        edgeBridge.ConnectionChanged += connected => { if (!connected) DispatchUi(() => { if (bridgeSnapshot is { } snapshot && edgeBridge.IsCurrentConnection(snapshot.ConnectionGeneration)) return; bridgeSnapshot = null; favoriteAdd.Enabled = false; UpdateEdgeConnectionButton(); edgeFavorites.Reset(); _ = RefreshFavorites(); }); };
        edgeBridge.ProfilesChanged += () => DispatchUi(UpdateEdgeConnectionButton);
        favoritesTimer.Tick += async (_, _) => await RefreshFavorites();
        favoritesTimer.Start();
        sessionTimer.Tick += (_, _) => { sessionTimer.Stop(); SaveSession(); };
        historyTimer.Tick += (_, _) => { historyTimer.Stop(); addressHistory.Save(); };
        favoriteIconSaveTimer.Tick += (_, _) => { favoriteIconSaveTimer.Stop(); _ = Task.Run(favoritePageIconCache.Save); };
        updateTimer.Tick += async (_, _) => await CheckForUpdates(false);
        back.Click += (_, _) => { if (active?.View.CoreWebView2?.CanGoBack == true) active.View.CoreWebView2.GoBack(); };
        forward.Click += (_, _) => { if (active?.View.CoreWebView2?.CanGoForward == true) active.View.CoreWebView2.GoForward(); };
        reload.Click += (_, _) => { if (active?.Failed == true) RetryFailedPage(); else active?.View.CoreWebView2?.Reload(); };
        home.Click += (_, _) => Navigate("about:blank");
        downloads.Click += async (_, _) => await OpenDownloads();
        menuButton.Click += (_, _) => ShowBrowserMenu();
        address.KeyDown += (_, e) => {
            if (suggestionPopup.Visible && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
            {
                int next = e.KeyCode == Keys.Down ? Math.Min(addressSuggestions.Items.Count - 1, addressSuggestions.SelectedIndex + 1) : Math.Max(0, addressSuggestions.SelectedIndex < 0 ? addressSuggestions.Items.Count - 1 : addressSuggestions.SelectedIndex - 1);
                addressSuggestions.SelectedIndex = next; e.SuppressKeyPress = true; return;
            }
            if (e.KeyCode == Keys.Escape && suggestionPopup.Visible) { DismissAddressSuggestions(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Enter)
            {
                submittingAddress = true;
                try { string target = addressSuggestions.SelectedItem is AddressSuggestion suggestion && suggestionPopup.Visible ? suggestion.Url : address.Text; suggestionPopup.Close(); Navigate(target); active?.View.Focus(); }
                finally { submittingAddress = false; }
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape && active != null)
            { SetDisplayedAddress(active.IsNewTab ? "" : active.Url); if (!active.IsNewTab) active.View.Focus(); e.SuppressKeyPress = true; }
        };
        address.TextChanged += (_, _) => { if (address.Focused && !submittingAddress) ShowAddressSuggestions(); };
        address.Leave += (_, _) => {
            if (showingSuggestions || suggestionPopup.Visible)
            {
                CloseSuggestionsWhenUnfocused();
            }
            else RestoreAddressAfterEditing();
        };
        HandleCreated += (_, _) => NativeCaption.Apply(this);
        Shown += async (_, _) => await StartBrowser();
        Shown += async (_, _) => await RefreshFavorites();
        Shown += (_, _) => edgeBridge.Start();
        backgroundMemoryTimer.Tick += (_, _) => ApplyBackgroundMemoryPolicy();
        Shown += (_, _) => backgroundMemoryTimer.Start();
        Shown += async (_, _) => { try { await Task.Delay(TimeSpan.FromSeconds(10), updateCancellation.Token); if (!IsDisposed) { updateTimer.Start(); await CheckForUpdates(false); } } catch (OperationCanceledException) { } };
        FormClosing += (_, e) => {
            if (e.CloseReason == CloseReason.UserClosing && !downloadCloseConfirmedForUpdate && !ConfirmClosingWithDownloads()) { e.Cancel = true; return; }
            DismissAddressSuggestions(); favoritesTimer.Stop(); sessionTimer.Stop(); historyTimer.Stop(); favoriteIconSaveTimer.Stop(); backgroundMemoryTimer.Stop(); updateTimer.Stop(); updateCancellation.Cancel(); favoriteIconLoad?.Cancel(); SaveSession(); addressHistory.Save(); favoritePageIconCache.Save();
        };
        FormClosed += (_, _) => { suggestionSearch?.Cancel(); foreach (var icon in suggestionIcons.Values) icon.Dispose(); suggestionIcons.Clear(); suggestionBoldFont.Dispose(); sessionTimer.Dispose(); historyTimer.Dispose(); favoritesTimer.Dispose(); railCollapseTimer.Dispose(); updateTimer.Dispose(); suggestionFocusTimer.Dispose(); suggestionPopup.Dispose(); edgeBridge.Dispose(); };
        KeyDown += OnShortcut;
    }

    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) { backgroundMemoryTimer.Dispose(); favoriteIconSaveTimer.Dispose(); tooltip.Dispose(); } }
    }

    internal async void OpenExternal(string[] args)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        foreach (string url in args.Where(x => !x.StartsWith('-')))
        {
            if (!sessionRestored || environment is null || browserProcessFailed || recoveryInProgress) { pendingExternalUrls.Add(url); ScheduleSessionSave(); }
            else await AddTab(Normalize(url));
        }
    }

    private void ShowBrowserMenu()
    {
        var menu = new ChromeContextMenu();
        string? source = active?.Url;
        var host = Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.Host : null;
        var block = new ToolStripMenuItem("本网站广告拦截") { Checked = adBlock.EnabledFor(source), Enabled = host != null && !browserProcessFailed && !recoveryInProgress };
        block.Click += (_, _) => {
            if (host is null) return;
            try { adBlock.SetEnabled(host, !adBlock.EnabledFor(source)); active?.View.CoreWebView2?.Reload(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ChromeMessage.Show(this, $"无法保存设置：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        menu.Items.Add(block);
        var reopen = new ToolStripMenuItem("重新打开关闭的标签页") { Enabled = recentlyClosed.Count > 0 && !browserProcessFailed && !recoveryInProgress, ShortcutKeyDisplayString = "Ctrl+Shift+T" };
        reopen.Click += async (_, _) => await ReopenClosedTab();
        menu.Items.Add(reopen);
        var find = new ToolStripMenuItem("在页面中查找") { Enabled = CanFindInPage(), ShortcutKeyDisplayString = "Ctrl+F" };
        find.Click += async (_, _) => await OpenPageFind();
        menu.Items.Add(find);
        var updateNetwork = new ToolStripMenuItem("更新网络设置") { Enabled = !updateBusy };
        updateNetwork.Click += (_, _) => ConfigureUpdateNetwork(); menu.Items.Add(updateNetwork);
        var browserNetwork = new ToolStripMenuItem("代理和 Agent 调试");
        browserNetwork.Click += (_, _) => ConfigureBrowserNetwork(); menu.Items.Add(browserNetwork);
        var downloadSettings = new ToolStripMenuItem("下载设置");
        downloadSettings.Click += (_, _) => ConfigureDownloads(); menu.Items.Add(downloadSettings);
        DisposeMenuAfterClose(menu); menu.Show(menuButton, new Point(0, menuButton.Height));
    }

    private void UpdateEdgeConnectionButton()
    {
        var choices = edgeBridge.GetConnections();
        bool connected = bridgeSnapshot is { } snapshot && edgeBridge.IsCurrentConnection(snapshot.ConnectionGeneration);
        favoriteConnect.ForeColor = connected ? BrowserPalette.Accent : BrowserPalette.Ink;
        favoriteConnect.Text = connected ? "已连接" : choices.Count > 0 ? "选择" : edgeBridge.WaitingForSelection ? "等待" : "同步";
        favoriteConnect.AccessibleName = connected ? "Edge 收藏已连接，选择配置" : choices.Count > 0 ? "选择 Edge 收藏配置" : "连接 Edge 收藏同步";
        tooltip.SetToolTip(favoriteConnect, connected ? $"Edge 收藏已连接，点击选择配置（{choices.Count} 个可用）{(edgeBridge.SelectionSaved ? "" : "；选择仅在本次会话有效")}" :
            choices.Count > 0 ? "点击根据收藏预览选择 Edge 配置" : edgeBridge.WaitingForSelection ? "等待所选 Edge 配置连接；点击查看同步设置" : "连接 Edge 收藏同步");
    }

    private void ShowEdgeConnections()
    {
        var choices = edgeBridge.GetConnections();
        if (choices.Count == 0) { EdgeFavoritesSetup.ShowInstructions(this); return; }
        var menu = CreateEdgeConnectionsMenu(choices);
        DisposeMenuAfterClose(menu); menu.Show(favoriteConnect, new Point(0, favoriteConnect.Height));
    }

    private ChromeContextMenu CreateEdgeConnectionsMenu(IReadOnlyList<EdgeFavoritesBridge.ConnectionChoice> choices)
    {
        var menu = new ChromeContextMenu();
        menu.Items.Add(new ToolStripMenuItem("Edge 收藏来源") { Enabled = false });
        foreach (var choice in choices)
        {
            string profile = choice.ProfileId is { } id ? $"配置 {id[..8]}" : "旧版扩展连接";
            string label = ($"{profile} · {choice.Preview}" + (choice.Ambiguous ? "（标识重复）" : "")).Replace("&", "&&");
            var item = new ToolStripMenuItem(label) { Checked = choice.Selected, Enabled = !choice.Ambiguous };
            item.Click += (_, _) => {
                try
                {
                    if (!edgeBridge.SelectConnection(choice.Generation))
                        ChromeMessage.Show(this, "本次已切换，但无法保存 Edge 配置选择。请检查本地设置文件，重新打开后需要再次选择。", "Edge 收藏");
                }
                catch (InvalidOperationException ex) { ChromeMessage.Show(this, ex.Message, "Edge 收藏"); }
            };
            menu.Items.Add(item);
        }
        if (choices.Any(choice => choice.Ambiguous))
            menu.Items.Add(new ToolStripMenuItem("标识重复：先停用其中一个配置的扩展") { Enabled = false });
        if (choices.Any(choice => choice.ProfileId is null))
            menu.Items.Add(new ToolStripMenuItem("旧版扩展：请在同步设置中更新并重新加载") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("同步设置与扩展安装", null, (_, _) => EdgeFavoritesSetup.ShowInstructions(this));
        return menu;
    }

    private void SetUpdateButton(string? busyText = null, string? detail = null)
    {
        if (IsDisposed || Disposing) return;
        updateButton.Enabled = busyText is null;
        updateButton.Text = busyText ?? (availableUpdate is null ? "更新" : "有更新");
        updateButton.ForeColor = availableUpdate is null ? BrowserPalette.Ink : BrowserPalette.Accent;
        updateButton.AccessibleName = availableUpdate is null ? "检查更新" : "有新版本，点击更新";
        tooltip.SetToolTip(updateButton, detail ?? (availableUpdate is null ? "检查 GitHub 更新" : $"发现 {availableUpdate.Version.ToString(3)}，点击更新"));
    }

    private async Task CheckForUpdates(bool interactive)
    {
        if (updateBusy || IsDisposed || Disposing) return;
        updateBusy = true; SetUpdateButton("检查中");
        UpdateCheckResult? result = null;
        string? failure = null;
        try
        {
            result = await Task.Run(() => updateService.CheckAsync(updateCancellation.Token), updateCancellation.Token);
            availableUpdate = result.Package;
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { failure = "连接 GitHub 超时。请检查系统代理或在菜单中配置更新网络。"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { failure = $"检查更新失败：{ex.Message}"; }
        finally { updateBusy = false; SetUpdateButton(detail: failure); }
        if (!interactive || IsDisposed || Disposing) return;
        if (failure != null) { ChromeMessage.Show(this, failure, "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (result?.Package is { } package) await DownloadAndConfirmUpdate(package);
        else if (result != null) ChromeMessage.Show(this, $"当前版本 {result.Current.ToString(3)}；GitHub 最新正式版 {result.Latest.ToString(3)}。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task DownloadAndConfirmUpdate(UpdatePackage package)
    {
        if (updateBusy || IsDisposed || Disposing) return;
        updateBusy = true; SetUpdateButton("下载中");
        string? installer = null;
        string? failure = null;
        try { installer = await Task.Run(() => updateService.DownloadAsync(package, updateCancellation.Token), updateCancellation.Token); }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { failure = "下载安装包超时。请检查更新网络设置。"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { failure = $"无法下载更新：{ex.Message}"; }
        finally { updateBusy = false; SetUpdateButton(detail: failure); }
        if (IsDisposed || Disposing) return;
        if (failure != null) { ChromeMessage.Show(this, failure, "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (installer is null) return;
        if (ChromeMessage.Show(this, $"已下载并校验版本 {package.Version.ToString(3)}。安装时需要关闭浏览器，是否继续？", "确认更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!ConfirmClosingWithDownloads()) return;
        try
        {
            var start = new ProcessStartInfo("msiexec.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/i"); start.ArgumentList.Add(installer);
            if (Process.Start(start) is null) throw new InvalidOperationException("无法启动 Windows 安装程序。");
            downloadCloseConfirmedForUpdate = true;
            try { Close(); } finally { downloadCloseConfirmedForUpdate = false; }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { ChromeMessage.Show(this, $"无法启动安装程序：{ex.Message}", "更新", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private int PendingDownloadCount() => browserProcessFailed ? 0 : tabs.Sum(tab => tab.Downloads.Count) + retainedDownloadTabs.Sum(tab => tab.Downloads.Count);

    private bool ConfirmClosingWithDownloads()
    {
        int count = PendingDownloadCount();
        if (count == 0) return true;
        using var dialog = CreateDownloadCloseDialog(count);
        return dialog.ShowChromeDialog(this) == DialogResult.Yes;
    }

    private static ChromeDialog CreateDownloadCloseDialog(int count)
    {
        var dialog = ChromeMessage.Create($"还有 {count} 个下载尚未完成。关闭浏览器会中断下载，并可能无法继续。", "下载尚未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        var close = (Button)dialog.AcceptButton!;
        var keep = (Button)dialog.CancelButton!;
        close.Text = "关闭浏览器";
        keep.Text = "保留浏览器";
        keep.TabIndex = 0; close.TabIndex = 1;
        dialog.AcceptButton = keep;
        dialog.Shown += (_, _) => keep.Select();
        return dialog;
    }

    private void ConfigureUpdateNetwork()
    {
        using var dialog = new ChromeDialog("更新网络设置", new Size(620, 385));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(26, 0, 26, 20) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var proxyInput = new ChromeInput { Dock = DockStyle.Fill }; var proxy = proxyInput.Field; proxy.Text = updateService.Network.ProxyUrl ?? ""; proxy.AccessibleName = "更新代理地址";
        var mirrorInput = new ChromeInput { Dock = DockStyle.Fill }; var mirror = mirrorInput.Field; mirror.Text = updateService.Network.MirrorUrl ?? ""; mirror.AccessibleName = "下载镜像前缀";
        layout.Controls.Add(new Label { Text = "代理地址（留空使用系统代理，不填写账号密码）", Dock = DockStyle.Fill }, 0, 0); layout.Controls.Add(proxyInput, 0, 1);
        layout.Controls.Add(new Label { Text = "下载镜像前缀（留空使用 gh.llkk.cc，必须使用 HTTPS）", Dock = DockStyle.Fill }, 0, 2); layout.Controls.Add(mirrorInput, 0, 3);
        layout.Controls.Add(new Label { Text = "版本与校验值来自 GitHub 官方接口或发布页；下载失败时使用镜像。", Dock = DockStyle.Fill }, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new ChromeButton { Text = "取消", DialogResult = DialogResult.Cancel };
        var save = new ChromeButton { Text = "保存" };
        save.Click += (_, _) => {
            try { updateService.SaveNetwork(proxy.Text, mirror.Text); availableUpdate = null; SetUpdateButton(); dialog.DialogResult = DialogResult.OK; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ChromeMessage.Show(dialog, ex.Message, "更新网络设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 5); dialog.Body.Controls.Add(layout); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowChromeDialog(this) == DialogResult.OK) _ = CheckForUpdates(false);
    }

    private void DispatchUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated)
            try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void DisposeMenuAfterClose(ContextMenuStrip menu)
    {
        menu.Closed += (_, _) => {
            // ToolStrip still unwinds its Close/activation state after Closed.
            // Disposing synchronously inside that event can recreate a handle
            // on an already disposed menu during the same message dispatch.
            if (!menu.IsDisposed && menu.IsHandleCreated)
                try { menu.BeginInvoke(menu.Dispose); return; } catch (InvalidOperationException) { }
            SynchronizationContext.Current?.Post(_ => menu.Dispose(), null);
        };
    }

    private async Task RefreshFavorites()
    {
        if (refreshingFavorites || bridgeSnapshot != null) return;
        if (edgeBridge.WaitingForSelection)
        {
            if (displayedFavorites is null && (favoritesBar.Items.Count != 1 || favoritesBar.Items[0].Text != "等待所选 Edge 配置连接"))
            {
                favoritesBar.SuspendLayout();
                var previous = favoritesBar.Items.Cast<ToolStripItem>().ToArray();
                favoritesBar.Items.Clear();
                foreach (var item in previous) item.Dispose();
                favoritesBar.Items.Add(new ToolStripLabel("等待所选 Edge 配置连接") { ForeColor = Color.Gray });
                favoritesBar.ResumeLayout();
            }
            return;
        }
        if (edgeBridge.Connected) return;
        refreshingFavorites = true;
        try
        {
            var snapshot = await edgeFavorites.ReadIfChangedAsync();
            if (snapshot is null || IsDisposed || Disposing || bridgeSnapshot != null || edgeBridge.WaitingForSelection || edgeBridge.Connected) return;
            ApplyFavorites(snapshot);
        }
        finally { refreshingFavorites = false; }
    }

    private void ApplyFavorites(FavoriteSnapshot snapshot)
    {
        displayedFavorites = snapshot;
        favoriteIcons = new(StringComparer.Ordinal);
        favoritePageIndex = new(StringComparer.Ordinal);
        favoriteIconLoad?.Cancel(); favoriteIconLoad?.Dispose();
        favoriteIconLoad = new CancellationTokenSource();
        _ = LoadFavoriteIcons(snapshot, favoriteIconLoad.Token);
        favoritesBar.SuspendLayout();
        var previous = favoritesBar.Items.Cast<ToolStripItem>().ToArray();
        favoritesBar.Items.Clear();
        foreach (var item in previous) item.Dispose();
        foreach (var node in snapshot.Bar) favoritesBar.Items.Add(CreateFavoriteItem(node, sourceGeneration: snapshot.ConnectionGeneration));
        if (snapshot.Bar.Count == 0) favoritesBar.Items.Add(new ToolStripLabel(snapshot.Available ? "Edge 收藏栏为空" : "无法读取 Edge 当前配置") { ForeColor = Color.Gray });
        if (snapshot.Other is { } other) favoritesBar.Items.Add(CreateFavoriteItem(other, isRoot: true, sourceGeneration: snapshot.ConnectionGeneration));
        if (snapshot.AdditionalRoots is { } roots)
            foreach (var root in roots) favoritesBar.Items.Add(CreateFavoriteItem(root, isRoot: true, sourceGeneration: snapshot.ConnectionGeneration));
        favoritesBar.ResumeLayout();
        if (suggestionPopup.Visible && address.Focused) ShowAddressSuggestions();
    }

    private void SetDisplayedAddress(string text)
    {
        bool previous = submittingAddress; submittingAddress = true;
        try { address.Text = text; }
        finally { submittingAddress = previous; }
    }
    private void DismissAddressSuggestions()
    {
        suggestionGeneration++;
        suggestionSearch?.Cancel();
        suggestionPopup.Close();
    }
    private void DismissSuggestionsIfBackground()
    {
        if (!IsDisposed && suggestionPopup.Visible && (WindowState == FormWindowState.Minimized || !Visible || !NativeForeground.IsCurrentProcess() || (!showingSuggestions && !address.Focused && !addressSuggestions.Focused)))
            DismissAddressSuggestions();
    }
    private void RestoreAddressAfterEditing()
    {
        if (!submittingAddress && !browserProcessFailed && !recoveryInProgress && active != null) SetDisplayedAddress(active.IsNewTab ? "" : active.Url);
    }
    private void CloseSuggestionsWhenUnfocused()
    {
        if (IsHandleCreated && !IsDisposed)
            try { BeginInvoke(() => { if (!IsDisposed && !address.Focused && !addressSuggestions.Focused) { DismissAddressSuggestions(); RestoreAddressAfterEditing(); } }); }
            catch (InvalidOperationException) { }
    }
    private async void ShowAddressSuggestions()
    {
        if (addressPanel is null || suggestionHost is null || IsDisposed || !address.Focused || WindowState == FormWindowState.Minimized || !NativeForeground.IsCurrentProcess()) return;
        suggestionSearch?.Cancel();
        var cancellation = new CancellationTokenSource();
        suggestionSearch = cancellation;
        int generation = ++suggestionGeneration;
        string input = address.Text;
        var favorites = displayedFavorites;
        if (string.IsNullOrWhiteSpace(input)) { suggestionPopup.Close(); addressSuggestions.Items.Clear(); cancellation.Dispose(); suggestionSearch = null; return; }
        try
        {
            string target = Normalize(input);
            bool search = target == "https://www.google.com/search?q=" + Uri.EscapeDataString(input.Trim());
            var primary = new AddressSuggestion(input.Trim(), target, false, 0, 0, Primary: true, Search: search);
            SetAddressSuggestions(primary, []);
            ShowSuggestionPopup();
            await Task.Delay(80, cancellation.Token);
            var matches = await addressHistory.SearchAsync(input, favorites, cancellation.Token);
            if (IsDisposed || Disposing || !address.Focused || WindowState == FormWindowState.Minimized || !NativeForeground.IsCurrentProcess() || generation != suggestionGeneration ||
                address.Text != input || !ReferenceEquals(displayedFavorites, favorites)) return;
            SetAddressSuggestions(primary, matches);
            ShowSuggestionPopup();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(suggestionSearch, cancellation)) suggestionSearch = null; cancellation.Dispose(); }
    }
    private void SetAddressSuggestions(AddressSuggestion primary, IReadOnlyList<AddressSuggestion> matches)
    {
        string? selected = addressSuggestions.SelectedIndex > 0 && addressSuggestions.SelectedItem is AddressSuggestion current ? current.Url : null;
        suggestionHover = -1;
        addressSuggestions.BeginUpdate();
        try
        {
            addressSuggestions.Items.Clear(); addressSuggestions.Items.Add(primary);
            foreach (var item in matches) if (!string.Equals(item.Url, primary.Url, StringComparison.Ordinal)) addressSuggestions.Items.Add(item);
            int index = selected is null ? 0 : Enumerable.Range(1, addressSuggestions.Items.Count - 1).FirstOrDefault(i => ((AddressSuggestion)addressSuggestions.Items[i]).Url == selected);
            addressSuggestions.SelectedIndex = index;
        }
        finally { addressSuggestions.EndUpdate(); }
    }
    private void ShowSuggestionPopup()
    {
        if (addressPanel is null || WindowState == FormWindowState.Minimized || !NativeForeground.IsCurrentProcess()) return;
        Point location = PositionAddressSuggestions();
        if (suggestionPopup.Visible) return;
        showingSuggestions = true;
        try
        {
            suggestionPopup.ShowAttached(addressPanel, addressPanel.PointToClient(location));
            if (!NativeForeground.IsCurrentProcess()) DismissAddressSuggestions();
        }
        finally { showingSuggestions = false; }
    }
    private Point PositionAddressSuggestions()
    {
        if (addressPanel is null || suggestionHost is null) return Point.Empty;
        var bounds = Screen.FromControl(addressPanel).WorkingArea;
        Point below = addressPanel.PointToScreen(new Point(0, addressPanel.Height));
        Point above = addressPanel.PointToScreen(Point.Empty);
        Point windowRight = PointToScreen(new Point(ClientSize.Width, 0));
        int width = Math.Min(Math.Max(addressPanel.Width, windowRight.X - below.X - LogicalToDeviceUnits(10)), Math.Max(1, bounds.Width - 8));
        addressSuggestions.ItemHeight = LogicalToDeviceUnits(36);
        int desiredHeight = addressSuggestions.ItemHeight * addressSuggestions.Items.Count;
        int availableBelow = Math.Max(0, bounds.Bottom - below.Y - 4);
        int availableAbove = Math.Max(0, above.Y - bounds.Top - 4);
        bool openAbove = availableBelow < desiredHeight && availableAbove > availableBelow;
        int height = Math.Min(desiredHeight, Math.Max(1, (openAbove ? availableAbove : availableBelow) - 2));
        suggestionHost.Size = new Size(Math.Max(1, width - suggestionPopup.Padding.Horizontal), height); addressSuggestions.Size = suggestionHost.Size;
        Point location = new(Math.Clamp(below.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - width - 2)), openAbove ? above.Y - height - suggestionPopup.Padding.Vertical - 2 : below.Y);
        if (suggestionPopup.Visible) suggestionPopup.Location = location;
        return location;
    }
    private void DrawAddressSuggestion(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= addressSuggestions.Items.Count || addressSuggestions.Items[e.Index] is not AddressSuggestion item) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool highContrast = SystemInformation.HighContrast;
        Color background = highContrast ? (selected ? SystemColors.Highlight : SystemColors.Window) : selected ? Color.FromArgb(233, 237, 243) : e.Index == suggestionHover ? Color.FromArgb(245, 247, 250) : Color.White;
        Color foreground = highContrast ? (selected ? SystemColors.HighlightText : SystemColors.WindowText) : Color.FromArgb(42, 47, 54);
        using (var fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, e.Bounds);
        if (selected && !highContrast) { using var accent = new SolidBrush(BrowserPalette.Accent); e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Top, LogicalToDeviceUnits(3), e.Bounds.Height); }
        int iconSize = LogicalToDeviceUnits(16), iconLeft = e.Bounds.Left + LogicalToDeviceUnits(16);
        var iconBounds = new Rectangle(iconLeft, e.Bounds.Top + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize);
        if (!item.Primary && GetSuggestionIcon(item) is { } icon) e.Graphics.DrawImage(icon, iconBounds);
        else DrawSuggestionGlyph(e.Graphics, iconBounds, item.Primary && item.Search, item.Favorite && !item.Primary, foreground);
        int left = e.Bounds.Left + LogicalToDeviceUnits(44), right = e.Bounds.Right - LogicalToDeviceUnits(14);
        int available = Math.Max(0, right - left); if (available == 0) return;
        string title = string.IsNullOrWhiteSpace(item.Title) && Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : item.Title;
        var titleFont = item.Primary ? addressSuggestions.Font : suggestionBoldFont;
        int titleMax = item.Primary ? Math.Max(0, available - LogicalToDeviceUnits(95)) : Math.Max(0, Math.Min(available * 45 / 100, LogicalToDeviceUnits(330)));
        int titleWidth = Math.Min(titleMax, TextRenderer.MeasureText(e.Graphics, title, titleFont, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(e.Graphics, title, titleFont, new Rectangle(left, e.Bounds.Top, titleWidth, e.Bounds.Height), foreground, flags);
        int secondLeft = left + titleWidth + LogicalToDeviceUnits(8);
        string secondary = item.Primary ? (item.Search ? "— Google 搜索" : "— 转到网址") : "— " + DisplaySuggestionUrl(item.Url);
        Color secondaryColor = highContrast ? foreground : item.Primary ? Color.FromArgb(108, 116, 128) : BrowserPalette.Accent;
        if (secondLeft < right) TextRenderer.DrawText(e.Graphics, secondary, addressSuggestions.Font, new Rectangle(secondLeft, e.Bounds.Top, right - secondLeft, e.Bounds.Height), secondaryColor, flags);
    }
    private static string DisplaySuggestionUrl(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return url[7..];
        return url;
    }
    private Image? GetSuggestionIcon(AddressSuggestion item)
    {
        if (suggestionIcons.TryGetValue(item.Url, out var existing)) return existing;
        if (favoriteIcons.TryGetValue(item.Url, out var bytes))
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false); using var source = Image.FromStream(stream);
                var image = new Bitmap(source, new Size(16, 16));
                if (suggestionIcons.Count >= 48) { var oldest = suggestionIcons.First(); suggestionIcons.Remove(oldest.Key); oldest.Value.Dispose(); }
                suggestionIcons[item.Url] = image; return image;
            }
            catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
        }
        foreach (var tab in tabs)
            if (tab.Button.Image != null && tab.IconSource is { } iconSource && FavoriteIcons.SameOrigin(item.Url, iconSource)) return tab.Button.Image;
        return null;
    }
    private static void DrawSuggestionGlyph(Graphics graphics, Rectangle bounds, bool search, bool favorite, Color color)
    {
        var state = graphics.Save(); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = bounds.Width / 16f, x = bounds.Left, y = bounds.Top;
        using var pen = new Pen(color, Math.Max(1.2f, s * 1.3f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        if (search) { graphics.DrawEllipse(pen, x + 2 * s, y + 2 * s, 9 * s, 9 * s); graphics.DrawLine(pen, x + 10 * s, y + 10 * s, x + 14 * s, y + 14 * s); }
        else if (favorite)
        {
            var points = Enumerable.Range(0, 10).Select(i => { double angle = -Math.PI / 2 + i * Math.PI / 5; float radius = (i % 2 == 0 ? 7 : 3.2f) * s; return new PointF(x + 8 * s + (float)Math.Cos(angle) * radius, y + 8 * s + (float)Math.Sin(angle) * radius); }).ToArray();
            graphics.DrawPolygon(pen, points);
        }
        else { graphics.DrawEllipse(pen, x + s, y + s, 14 * s, 14 * s); graphics.DrawArc(pen, x + 4 * s, y + s, 8 * s, 14 * s, 90, 180); graphics.DrawArc(pen, x + 4 * s, y + s, 8 * s, 14 * s, 270, 180); graphics.DrawLine(pen, x + 2 * s, y + 8 * s, x + 14 * s, y + 8 * s); }
        graphics.Restore(state);
    }
    private void UseAddressSuggestion()
    {
        if (addressSuggestions.SelectedItem is not AddressSuggestion item) return;
        submittingAddress = true;
        try { suggestionPopup.Close(); Navigate(item.Url); active?.View.Focus(); }
        finally { submittingAddress = false; }
    }
    private void RecordHistory(string? url, string? title, bool countVisit = true)
    {
        if (!addressHistory.Record(url, title, countVisit)) return;
        historyTimer.Stop(); historyTimer.Start();
    }

    private FavoriteSnapshot? WritableFavorites(long? sourceGeneration = null)
    {
        var snapshot = bridgeSnapshot;
        if (snapshot is not null && (!sourceGeneration.HasValue || sourceGeneration.Value == snapshot.ConnectionGeneration) && edgeBridge.IsCurrentConnection(snapshot.ConnectionGeneration)) return snapshot;
        ChromeMessage.Show(this, "Edge 收藏连接已变化，请重新选择收藏后再试。", "收藏同步", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }

    private async Task AddCurrentFavorite(string? parentId = null, long? sourceGeneration = null)
    {
        if (browserProcessFailed || recoveryInProgress || active?.View.CoreWebView2 is not { } core) return;
        if (WritableFavorites(sourceGeneration) is not { } snapshot) return;
        string destination = parentId ?? snapshot.BarId;
        if (string.IsNullOrWhiteSpace(destination)) { ChromeMessage.Show(this, "当前 Edge 配置没有可写的收藏栏。", "收藏同步"); return; }
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { ChromeMessage.Show(this, "只能收藏 HTTP 或 HTTPS 网页。", "agbrowser"); return; }
        try { await edgeBridge.ExecuteAsync(snapshot.ConnectionGeneration, "create", parentId: destination, title: core.DocumentTitle, url: uri.AbsoluteUri); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { ChromeMessage.Show(this, $"无法添加收藏：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private string? PromptName(string title, string value = "")
    {
        using var dialog = new ChromeDialog(title, new Size(460, 190));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 0, 26, 16), RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = new ChromeInput { Dock = DockStyle.Fill }; var field = input.Field; field.Text = value; field.AccessibleName = "名称";
        var save = new ChromeButton { Text = "确定", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        layout.Controls.Add(input, 0, 0); layout.Controls.Add(save, 0, 1); dialog.AcceptButton = save; dialog.Body.Controls.Add(layout);
        return dialog.ShowChromeDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(field.Text) ? field.Text.Trim() : null;
    }
    private async Task CreateFolder(string parentId, long sourceGeneration)
    {
        if (string.IsNullOrWhiteSpace(parentId)) return;
        if (WritableFavorites(sourceGeneration) is not { } snapshot) return;
        string? name = PromptName("新建收藏文件夹"); if (name is null) return;
        try { await edgeBridge.ExecuteAsync(snapshot.ConnectionGeneration, "create", parentId: parentId, title: name); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { ChromeMessage.Show(this, $"无法新建文件夹：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private async Task RenameFolder(FavoriteNode node, long sourceGeneration)
    {
        if (!node.CanModify) return;
        if (WritableFavorites(sourceGeneration) is not { } snapshot) return;
        string? name = PromptName("重命名收藏文件夹", node.Name); if (name is null) return;
        try { await edgeBridge.ExecuteAsync(snapshot.ConnectionGeneration, "update", bookmarkId: node.Id, title: name); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { ChromeMessage.Show(this, $"无法重命名文件夹：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task EditFavorite(FavoriteNode node, long sourceGeneration)
    {
        if (!node.CanModify) return;
        if (node.Url is null || WritableFavorites(sourceGeneration) is not { } snapshot) return;
        using var dialog = new ChromeDialog("编辑收藏", new Size(540, 260));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(26, 0, 26, 16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var nameInput = new ChromeInput { Dock = DockStyle.Fill }; var name = nameInput.Field; name.Text = node.Name; name.AccessibleName = "收藏名称";
        var urlInput = new ChromeInput { Dock = DockStyle.Fill }; var url = urlInput.Field; url.Text = node.Url; url.AccessibleName = "收藏地址";
        layout.Controls.Add(new Label { Text = "名称", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); layout.Controls.Add(nameInput, 1, 0);
        layout.Controls.Add(new Label { Text = "地址", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1); layout.Controls.Add(urlInput, 1, 1);
        var save = new ChromeButton { Text = "保存", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        layout.Controls.Add(save, 1, 2); dialog.AcceptButton = save; dialog.Body.Controls.Add(layout);
        if (dialog.ShowChromeDialog(this) != DialogResult.OK) return;
        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        { ChromeMessage.Show(this, "请输入 HTTP 或 HTTPS 地址。", "agbrowser"); return; }
        try { await edgeBridge.ExecuteAsync(snapshot.ConnectionGeneration, "update", bookmarkId: node.Id, title: name.Text.Trim(), url: uri.AbsoluteUri); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { ChromeMessage.Show(this, $"无法编辑收藏：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async Task RemoveFavorite(FavoriteNode node, long sourceGeneration)
    {
        if (!node.CanModify) return;
        if (WritableFavorites(sourceGeneration) is not { } snapshot) return;
        string description = node.Url is null ? $"文件夹“{node.Name}”及其中所有收藏" : $"收藏“{node.Name}”";
        if (ChromeMessage.Show(this, $"从 Edge 删除{description}？", "删除收藏", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await edgeBridge.ExecuteAsync(snapshot.ConnectionGeneration, node.Url is null ? "removeTree" : "remove", bookmarkId: node.Id); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        { ChromeMessage.Show(this, $"无法删除收藏：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private ContextMenuStrip CreateFavoriteFolderMenu(FavoriteNode node, bool isRoot, long sourceGeneration)
    {
        var menu = new ChromeContextMenu();
        if (!node.CanModify)
        {
            menu.Items.Add(new ToolStripMenuItem("此收藏夹由组织管理，只读") { Enabled = false });
            DisposeMenuAfterClose(menu);
            return menu;
        }
        menu.Items.Add("收藏当前页面到此文件夹", null, async (_, _) => await AddCurrentFavorite(node.Id, sourceGeneration));
        menu.Items.Add("新建子文件夹", null, async (_, _) => await CreateFolder(node.Id, sourceGeneration));
        if (!isRoot)
        {
            menu.Items.Add("重命名文件夹", null, async (_, _) => await RenameFolder(node, sourceGeneration));
            menu.Items.Add("删除文件夹", null, async (_, _) => await RemoveFavorite(node, sourceGeneration));
        }
        DisposeMenuAfterClose(menu);
        return menu;
    }

    private ContextMenuStrip CreateFavoriteLinkMenu(FavoriteNode node, long sourceGeneration)
    {
        var menu = new ChromeContextMenu();
        menu.Items.Add("编辑标签", null, (_, _) => EditFavoriteTags(node));
        if (sourceGeneration != 0 && node.CanModify)
        {
            menu.Items.Add("编辑收藏", null, async (_, _) => await EditFavorite(node, sourceGeneration));
            menu.Items.Add("从 Edge 删除", null, async (_, _) => await RemoveFavorite(node, sourceGeneration));
        }
        DisposeMenuAfterClose(menu);
        return menu;
    }

    private ToolStripItem CreateFavoriteItem(FavoriteNode node, bool isRoot = false, long sourceGeneration = 0)
    {
        string label = string.IsNullOrWhiteSpace(node.Name) ? (node.Url ?? "收藏夹") : node.Name;
        string caption = label.Length > 22 ? label[..21] + "…" : label;
        if (node.Url is { } url)
        {
            var link = new ToolStripButton(caption) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Image = FavoriteGlyphs.Site(), ToolTipText = label, AutoToolTip = false, Tag = url };
            ApplyFavoriteIcon(link, url);
            link.Disposed += (_, _) => link.Image?.Dispose();
            link.Click += (_, _) => {
                if (Uri.TryCreate(url, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeFile)) Navigate(url);
                else ChromeMessage.Show(this, "此类收藏地址暂不支持打开。", "agbrowser");
            };
            link.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && link.Owner != null) { var menu = CreateFavoriteLinkMenu(node, sourceGeneration); menu.Show(link.Owner, e.Location + new Size(link.Bounds.Location)); } };
            return link;
        }
        var folder = new ToolStripDropDownButton(caption) { DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, Image = FavoriteGlyphs.Folder(), ToolTipText = label, AutoToolTip = false };
        ChromeMenuStyle.Apply(folder.DropDown);
        folder.Disposed += (_, _) => folder.Image?.Dispose();
        folder.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && sourceGeneration != 0 && folder.Owner != null) {
            var menu = CreateFavoriteFolderMenu(node, isRoot, sourceGeneration);
            menu.Show(folder.Owner, e.Location + new Size(folder.Bounds.Location));
        } };
        folder.DropDownOpening += (_, _) => {
            foreach (ToolStripItem old in folder.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
            folder.DropDownItems.Clear();
            foreach (var child in node.Children) folder.DropDownItems.Add(CreateFavoriteItem(child, sourceGeneration: sourceGeneration));
            if (folder.DropDownItems.Count == 0) folder.DropDownItems.Add(new ToolStripMenuItem("空文件夹") { Enabled = false });
        };
        return folder;
    }

    private static Button Nav(string text) => new NavButton { Text = text, Dock = DockStyle.Fill, ForeColor = BrowserPalette.Ink, Font = new Font("Segoe UI", 15), Margin = Padding.Empty };

    private void UpdateFavoriteAvailability()
    {
        string? source = active?.View.CoreWebView2?.Source;
        bool webPage = Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        bool connected = bridgeSnapshot is { } snapshot && edgeBridge.IsCurrentConnection(snapshot.ConnectionGeneration);
        bool writableBar = connected && !string.IsNullOrWhiteSpace(bridgeSnapshot!.BarId);
        favoriteAdd.Enabled = writableBar && !browserProcessFailed && !recoveryInProgress && webPage;
        tooltip.SetToolTip(favoriteAdd, !connected ? "连接 Edge 收藏后可添加" : !writableBar ? "当前配置没有可写的收藏栏" : webPage ? "添加到 Edge 收藏栏" : "当前页面不能加入收藏");
    }

    private void StartRailCollapse()
    {
        if (!railExpanded || draggingTab != null || IsDisposed) return;
        railCollapseTimer.Stop(); railCollapseTimer.Start();
    }
    private void SetRailExpanded(bool expanded)
    {
        if (railExpanded == expanded || IsDisposed) return;
        int scrollY = -rail.AutoScrollPosition.Y;
        railExpanded = expanded;
        rail.SuspendLayout();
        try { foreach (var button in rail.Controls.OfType<RailTabButton>()) button.Expanded = expanded; updateRailWidth?.Invoke(); }
        finally { rail.ResumeLayout(); }
        rail.AutoScrollPosition = new Point(0, scrollY);
    }
    private bool IsTabDrag(DragEventArgs e) => draggingTab != null && tabs.Contains(draggingTab) && e.Data?.GetDataPresent(tabDragFormat) == true;
    private void ClearTabDragMarker() { dragMarker?.SetDropMarker(false, false); dragMarker = null; }
    private void TabDragOver(object? sender, DragEventArgs e)
    {
        if (!IsTabDrag(e)) { e.Effect = DragDropEffects.None; return; }
        e.Effect = DragDropEffects.Move;
        ClearTabDragMarker();
        if (sender is RailTabButton button)
        {
            int y = button.PointToClient(new Point(e.X, e.Y)).Y;
            dragMarker = button; button.SetDropMarker(y < button.Height / 2, y >= button.Height / 2);
        }
        int railY = rail.PointToClient(new Point(e.X, e.Y)).Y;
        int scroll = -rail.AutoScrollPosition.Y;
        if (railY < 24) rail.AutoScrollPosition = new Point(0, Math.Max(0, scroll - 20));
        else if (railY > rail.ClientSize.Height - 24) rail.AutoScrollPosition = new Point(0, scroll + 20);
    }
    private void MoveTab(BrowserTab tab, int index)
    {
        int source = tabs.IndexOf(tab);
        if (source < 0 || tabs.Count == 0 || recoveryInProgress) return;
        index = Math.Clamp(index, 0, tabs.Count - 1);
        if (source == index) return;
        tabs.RemoveAt(source); tabs.Insert(index, tab);
        rail.Controls.SetChildIndex(tab.Button, index);
        rail.ScrollControlIntoView(tab.Button);
        ScheduleSessionSave();
    }

    private async Task StartBrowser()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var settings = BrowserNetworkSettings.Load(dataDir);
            BrowserDownloadSettings.TryLoad(dataDir, out var downloadSettings, out _);
            // WebView2 may leave this file behind after a crash. Auto-connect
            // must not mistake an old port for the current browser session.
            debugPortWarning = BrowserNetworkSettings.ClearStaleDebugPort(dataDir);
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true, AdditionalBrowserArguments = string.Join(' ', settings.BrowserArguments(), downloadSettings.BrowserArguments()).Trim() };
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataDir, "profile"), options: options);
            runningParallelRequests = downloadSettings.EffectiveRequests;
            runningAgentDebug = settings.AgentDebug;
            environment.BrowserProcessExited += (_, e) => {
                if (e.BrowserProcessExitKind != CoreWebView2BrowserProcessExitKind.Failed) return;
                browserResourcesReleased = true;
                if (browserProcessFailed && !recoveryInProgress && !IsDisposed) ShowRecovery();
            };
            var state = LoadSession();
            var saved = state.Urls;
            if (saved.Length == 0 && initialUrls.Length == 0) saved = ["about:blank"];
            int selected = Math.Clamp(state.ActiveIndex, 0, Math.Max(0, saved.Length - 1));
            if (initialUrls.Length == 0 && saved.Length > 0)
            {
                // Begin loading the selected page before constructing the
                // inactive tabs, even when it was near the end of the rail.
                var selectedTab = await AddTab(Normalize(saved[selected]));
                if (selectedTab is null) return;
                BeginRailBatch();
                try
                {
                    for (int i = 0; i < saved.Length; i++)
                    {
                        if (i == selected) continue;
                        var tab = await AddTab(Normalize(saved[i]), lazy: true);
                        if (tab is null) return;
                        if (i < state.Urls.Length) RestoreTabAppearance(tab, state.Titles[i], state.Icons[i]);
                        tabs.Remove(tab); tabs.Insert(i, tab);
                        rail.Controls.SetChildIndex(tab.Button, i);
                    }
                }
                finally { EndRailBatch(); }
                rail.ScrollControlIntoView(selectedTab.Button);
            }
            else
            {
                BeginRailBatch();
                try
                {
                    for (int i = 0; i < saved.Length; i++)
                    {
                        var tab = await AddTab(Normalize(saved[i]), lazy: true);
                        if (tab is null) return;
                        if (i < state.Urls.Length) RestoreTabAppearance(tab, state.Titles[i], state.Icons[i]);
                    }
                }
                finally { EndRailBatch(); }
            }
            foreach (var url in initialUrls)
                if (await AddTab(Normalize(url)) is null) return;
            while (!IsDisposed && !Disposing && pendingExternalUrls.Count > 0)
            {
                string url = pendingExternalUrls[0];
                pendingExternalUrls.RemoveAt(0);
                if (await AddTab(Normalize(url)) is null) { pendingExternalUrls.Insert(0, url); break; }
            }
            sessionRestored = true;
            ScheduleSessionSave();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ChromeMessage.Show(this, "找不到 Microsoft Edge WebView2 Runtime。请从 Microsoft 官方网站安装 Evergreen Runtime 后重试。\r\nhttps://developer.microsoft.com/microsoft-edge/webview2/", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex) { ChromeMessage.Show(this, $"浏览器启动失败：{ex.Message}", "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void BeginRailBatch()
    {
        if (railMetricsBatchDepth++ != 0) return;
        rail.SuspendLayout(); content.SuspendLayout();
    }

    private void EndRailBatch()
    {
        if (--railMetricsBatchDepth != 0) return;
        content.ResumeLayout(); rail.ResumeLayout();
        if (railMetricsPending) updateRailWidth?.Invoke();
    }

    private async Task<BrowserTab?> AddTab(string? url, bool navigate = true, bool lazy = false, bool focusAddress = false)
    {
        if (environment is null || browserProcessFailed || recoveryInProgress) return null;
        var view = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        bool newTab = navigate && string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);
        var button = new RailTabButton { Expanded = railExpanded, DisplayTitle = newTab ? "新建标签页" : url ?? "about:blank", AllowDrop = true };
        var tab = new BrowserTab(view, button, url ?? "about:blank", navigate) { IsNewTab = newTab };
        tabs.Add(tab); rail.Controls.Add(button); button.RefreshDpiMetrics(); updateRailWidth?.Invoke(); content.Controls.Add(view);
        button.AccessibleName = newTab ? "新建标签页" : tab.Url; tooltip.SetToolTip(button, button.AccessibleName);
        if (newTab) button.Fallback = "+";
        if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var parsed) && !string.IsNullOrEmpty(parsed.Host))
            button.Fallback = parsed.Host[..1].ToUpperInvariant();
        button.Click += (_, _) => SelectTab(tab);
        button.MouseEnter += (_, _) => { railCollapseTimer.Stop(); SetRailExpanded(true); };
        button.MouseLeave += (_, _) => StartRailCollapse();
        button.CloseRequested += (_, _) => CloseTab(tab);
        button.MouseUp += (_, e) => { if (e.Button == MouseButtons.Middle) CloseTab(tab); };
        Point? dragStart = null;
        button.MouseDown += (_, e) => { dragStart = e.Button == MouseButtons.Left && !button.IsCloseHit(e.Location) ? e.Location : null; };
        button.MouseMove += (_, e) => {
            if (dragStart is not { } start || e.Button != MouseButtons.Left || draggingTab != null || recoveryInProgress) return;
            var size = SystemInformation.DragSize;
            if (new Rectangle(start.X - size.Width / 2, start.Y - size.Height / 2, size.Width, size.Height).Contains(e.Location)) return;
            dragStart = null; draggingTab = tab; railCollapseTimer.Stop();
            try { var data = new DataObject(); data.SetData(tabDragFormat, "tab"); button.DoDragDrop(data, DragDropEffects.Move); }
            finally { draggingTab = null; ClearTabDragMarker(); StartRailCollapse(); }
        };
        button.DragEnter += TabDragOver;
        button.DragOver += TabDragOver;
        button.DragLeave += (_, _) => ClearTabDragMarker();
        button.DragDrop += (_, e) => {
            if (IsTabDrag(e) && draggingTab is { } source)
            {
                int target = tabs.IndexOf(tab);
                if (button.PointToClient(new Point(e.X, e.Y)).Y >= button.Height / 2) target++;
                if (tabs.IndexOf(source) < target) target--;
                MoveTab(source, target);
            }
            ClearTabDragMarker();
        };
        var menu = new ChromeContextMenu(); menu.Items.Add("关闭标签页", null, (_, _) => DispatchUi(() => CloseTab(tab))); button.ContextMenuStrip = menu;
        if (lazy) return tab;
        SelectTab(tab);
        if (tab.IsNewTab)
        {
            if (focusAddress && active == tab && address.Focus()) address.Clear();
            // Keep auto-connect usable even when the session starts on an
            // empty tab, but do not create another renderer for every blank.
            if (runningAgentDebug && !tabs.Any(other => other != tab && (other.View.CoreWebView2 != null || other.Loading != null))) _ = EnsureActiveCore();
            return tab;
        }
        bool ready = await EnsureTab(tab);
        if (ready && focusAddress && active == tab && !IsDisposed && !browserProcessFailed && address.Focus())
        {
            if (tab.Url == "about:blank") address.Clear();
            else address.SelectAll();
        }
        return ready ? tab : null;
    }

    private Task<bool> EnsureTab(BrowserTab tab) => tab.Loading ??= InitializeTab(tab);

    private async Task<CoreWebView2?> EnsureActiveCore()
    {
        if (active is not { } tab || browserProcessFailed || recoveryInProgress) return null;
        if (tab.View.CoreWebView2 is null)
        {
            if (tab.IsNewTab) { tab.View.Visible = true; newTabPage.BringToFront(); loadingIndicator.Visible = true; }
            if (!await EnsureTab(tab)) return null;
        }
        if (tab.IsNewTab && tab.View.CoreWebView2?.IsDefaultDownloadDialogOpen != true)
        {
            tab.View.Visible = false;
            if (tab == active) { newTabPage.BringToFront(); loadingIndicator.Visible = tab.IsNavigating; }
        }
        return tab == active && tabs.Contains(tab) ? tab.View.CoreWebView2 : null;
    }
    private async Task OpenDownloads()
    {
        var tab = active;
        if (tab is null || await EnsureActiveCore() is not { } core || tab != active) return;
        if (tab.IsNewTab) { newTabPage.Visible = false; tab.View.Visible = true; tab.View.BringToFront(); }
        try { core.OpenDefaultDownloadDialog(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            if (tab == active && tab.IsNewTab && !IsDisposed) { tab.View.Visible = false; newTabPage.Visible = true; newTabPage.BringToFront(); }
            if (!IsDisposed && !Disposing) ChromeMessage.Show(this, "无法打开下载列表：" + ex.Message, "agbrowser");
        }
    }
    private async Task OpenDevTools() { if (await EnsureActiveCore() is { } core) core.OpenDevToolsWindow(); }

    private bool CanFindInPage() => !browserProcessFailed && !recoveryInProgress &&
        active is { IsNewTab: false, Failed: false, View.CoreWebView2: not null };

    private async Task<bool> OpenPageFind()
    {
        if (!CanFindInPage() || active is not { } tab || tab.View.CoreWebView2 is not { } core) return false;
        DismissAddressSuggestions();
        try
        {
            // Keep the WebView2/Chromium find bar and matching behavior. This
            // path is needed when focus is in the browser chrome, rather than
            // in the web view where WebView2 handles Ctrl+F itself.
            tab.View.Focus();
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = "";
            await core.Find.StartAsync(options);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (!IsDisposed && !Disposing && Visible && active == tab)
                ChromeMessage.Show(this, "无法打开页面查找，请检查 WebView2 Runtime 是否已更新。", "agbrowser");
            return false;
        }
    }

    private async Task<bool> InitializeTab(BrowserTab tab, bool preserveOnFailure = false)
    {
        var view = tab.View;
        var currentEnvironment = environment ?? throw new InvalidOperationException("Browser is not initialized");
        try
        {
            await view.EnsureCoreWebView2Async(currentEnvironment);
            if (!tabs.Contains(tab)) return false;
            var core = view.CoreWebView2;
            core.NavigationStarting += (_, e) => { if (!tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return; tab.TopLevelUrl = e.Uri; tab.IconGeneration++; tab.NavigationId = e.NavigationId; tab.IsNavigating = true; tab.Button.Loading = true; if (tab == active) loadingIndicator.Visible = true; };
            core.DocumentTitleChanged += (_, _) => { if (!tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return; UpdateTab(tab); if (tab.HistoryUrl == core.Source) RecordHistory(core.Source, core.DocumentTitle, countVisit: false); };
            core.SourceChanged += (_, _) => { if (browserProcessFailed || !tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return; tab.Url = core.Source; UpdateTab(tab); ScheduleSessionSave(); };
            core.HistoryChanged += (_, _) => { if (tabs.Contains(tab) && ReferenceEquals(tab.View, view)) UpdateTab(tab); };
            core.DownloadStarting += (_, e) => TrackDownload(tab, view, e.DownloadOperation);
            core.IsDefaultDownloadDialogOpenChanged += (_, _) => {
                if (!tab.IsNewTab || tab != active || !tabs.Contains(tab)) return;
                bool open = core.IsDefaultDownloadDialogOpen;
                tab.View.Visible = open; newTabPage.Visible = !open;
                if (open) tab.View.BringToFront(); else newTabPage.BringToFront();
            };
            core.FaviconChanged += async (_, _) => await UpdateIcon(tab);
            core.ProcessFailed += (_, e) => { if (ReferenceEquals(tab.View, view)) HandleProcessFailure(tab, e.ProcessFailedKind); };
            core.NavigationCompleted += async (_, e) => {
                if (!tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return;
                if (tab.NavigationId == e.NavigationId) { tab.IsNavigating = false; tab.Button.Loading = false; if (tab == active) loadingIndicator.Visible = false; }
                if (!e.IsSuccess || browserProcessFailed) return;
                tab.HistoryUrl = core.Source; RecordHistory(core.Source, core.DocumentTitle);
                try
                {
                    string source = core.Source;
                    if (AdBlockSettings.IsGoogleChallenge(source) || !adBlock.EnabledFor(source) || !Uri.TryCreate(source, UriKind.Absolute, out var page) ||
                        (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)) return;
                    // Checking the host again avoids a delayed result applying
                    // after navigation to a site with blocking disabled.
                    string hostname = JsonSerializer.Serialize(page.Host);
                    string script = $$"""
                        (() => {
                            if (location.hostname.toLowerCase() !== {{hostname}}.toLowerCase() || document.getElementById('agbrowser-ad-style')) return;
                            const style = document.createElement('style');
                            style.id = 'agbrowser-ad-style';
                            style.textContent = '.adsbygoogle,[id^="google_ads_"],[id^="div-gpt-ad"],.taboola-container,.outbrain-widget,.criteo-ad{display:none!important}';
                            (document.head || document.documentElement).appendChild(style);
                        })();
                        """;
                    await core.ExecuteScriptAsync(script);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException) { }
            };
            core.NewWindowRequested += async (_, e) => {
                if (!tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) { e.Handled = true; return; }
                var deferral = e.GetDeferral();
                try { var child = await AddTab(null, false); if (child != null) { child.ScriptOpened = true; child.Opener = tab; e.NewWindow = child.View.CoreWebView2; e.Handled = true; } }
                finally { deferral.Complete(); }
            };
            core.WindowCloseRequested += (_, _) => {
                // A website may close the window it opened, but must not be
                // allowed to close an ordinary user-created browser tab.
                if (tab.ScriptOpened && ReferenceEquals(tab.View, view) && !tab.ClosePending) FinishCloseTab(tab);
            };
            // Only send matching ad requests to the UI process. Leave ordinary
            // requests in the Chromium network stack.
            foreach (string host in BlockedHosts)
            {
                // The wildcard after the host also covers explicit ports. The
                // handler below validates the parsed hostname before blocking,
                // so similarly prefixed domains are not blocked.
                foreach (string pattern in new[] { $"*://{host}*/*", $"*://*.{host}*/*" })
                    core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
            }
            view.KeyDown += OnShortcut;
            core.WebResourceRequested += (_, e) => {
                // An explicitly requested top-level page remains accessible; ad
                // iframes are still blocked even though they are documents too.
                if (AdBlockSettings.IsGoogleChallenge(tab.TopLevelUrl) || !adBlock.EnabledFor(tab.TopLevelUrl)) return;
                if (e.ResourceContext == CoreWebView2WebResourceContext.Document &&
                    string.Equals(e.Request.Uri, tab.TopLevelUrl, StringComparison.OrdinalIgnoreCase)) return;
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) && BlockedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
                    e.Response = currentEnvironment.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
            if (tab == active)
            {
                if (tab.Navigate && !tab.IsNewTab)
                {
                    address.Enabled = true; reload.Enabled = true; home.Enabled = true; downloads.Enabled = true;
                    if (!address.Focused) SetDisplayedAddress(tab.Url);
                }
                else UpdateTab(tab);
            }
            if (tab.Navigate) core.Navigate(tab.Url);
            else if (tab == active) loadingIndicator.Visible = false;
            return true;
        }
        catch (Exception ex)
        {
            if (tabs.Contains(tab) && !preserveOnFailure) { CloseTab(tab, createReplacement: false); ChromeMessage.Show(this, ex.Message, "标签页启动失败"); }
            return false;
        }
    }

    private async Task UpdateIcon(BrowserTab tab)
    {
        if (browserProcessFailed || !tabs.Contains(tab)) return;
        var core = tab.View.CoreWebView2;
        long generation = ++tab.IconGeneration;
        string pageUrl = core.Source;
        var previous = tab.Button.Image;
        tab.Button.Image = null;
        tab.IconData = null;
        tab.IconSource = null;
        tab.Button.Invalidate();
        previous?.Dispose();
        ScheduleSessionSave();
        if (string.IsNullOrEmpty(core.FaviconUri)) return;
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (!tabs.Contains(tab) || tab.IconGeneration != generation || tab.Button.IsDisposed || core.Source != pageUrl) return;
            using var icon = Image.FromStream(stream);
            tab.Button.Image = new Bitmap(icon, new Size(24, 24));
            using var encoded = new MemoryStream();
            tab.Button.Image.Save(encoded, ImageFormat.Png);
            byte[] iconBytes = encoded.ToArray();
            tab.IconData = Convert.ToBase64String(iconBytes);
            tab.IconSource = pageUrl;
            ApplyLiveFavoriteIcon(pageUrl, iconBytes);
            tab.Button.Invalidate();
            ScheduleSessionSave();
        }
        catch { /* Some pages have no usable favicon. Keep the fallback. */ }
    }
    private void UpdateTab(BrowserTab tab, bool forceAddress = false)
    {
        if (browserProcessFailed) return;
        var core = tab.View.CoreWebView2; if (core is null) return;
        if (tab.IsNewTab)
        {
            if (tab == active) { address.Enabled = true; back.Enabled = false; forward.Enabled = false; reload.Enabled = true; home.Enabled = true; downloads.Enabled = true; Text = "新建标签页 — agbrowser"; }
            return;
        }
        if (tab == active) { address.Enabled = true; reload.Enabled = true; home.Enabled = true; downloads.Enabled = true; }
        var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? core.Source : core.DocumentTitle;
        if (tab.Title != title) { tab.Title = title; ScheduleSessionSave(); }
        tab.Button.AccessibleName = title; tab.Button.DisplayTitle = title; tab.Button.Invalidate(); tooltip.SetToolTip(tab.Button, title);
        if (tab.Button.Image is null && !string.IsNullOrWhiteSpace(title)) { tab.Button.Fallback = title[..1].ToUpperInvariant(); tab.Button.Invalidate(); }
        if (tab == active) { if (forceAddress || !address.Focused) SetDisplayedAddress(core.Source); Text = $"{title} — agbrowser"; back.Enabled = core.CanGoBack; forward.Enabled = core.CanGoForward; UpdateFavoriteAvailability(); }
    }
    private void SelectTab(BrowserTab tab)
    {
        if (recoveryInProgress) return;
        DismissAddressSuggestions();
        if (active != null)
        {
            if (active != tab) active.InactiveSince = Stopwatch.GetTimestamp();
            active.View.Visible = false; active.Button.Selected = false;
        }
        if (tab.LowMemoryApplied) SetTabMemoryTarget(tab, low: false);
        active = tab; tab.Button.Selected = true;
        newTabPage.Visible = false;
        if (tab.IsNewTab && !tab.Failed && !browserProcessFailed)
        {
            bool downloadsOpen = tab.View.CoreWebView2?.IsDefaultDownloadDialogOpen == true;
            tab.View.Visible = downloadsOpen; newTabPage.Visible = !downloadsOpen;
            if (downloadsOpen) tab.View.BringToFront(); else newTabPage.BringToFront();
            loadingIndicator.Visible = false; address.Enabled = true; back.Enabled = false; forward.Enabled = false;
            reload.Enabled = true; home.Enabled = true; downloads.Enabled = true;
            SetDisplayedAddress(""); Text = "新建标签页 — agbrowser";
            UpdateFavoriteAvailability(); rail.ScrollControlIntoView(tab.Button); ScheduleSessionSave();
            recovery.Visible = false;
            return;
        }
        tab.View.Visible = true; tab.View.BringToFront();
        loadingIndicator.Visible = tab.IsNavigating || tab.View.CoreWebView2 is null;
        UpdateFavoriteAvailability();
        rail.ScrollControlIntoView(tab.Button);
        ScheduleSessionSave();
        if (tab.Failed || browserProcessFailed) { ShowRecovery(); return; }
        recovery.Visible = false;
        if (tab.View.CoreWebView2 is null)
        {
            SetDisplayedAddress(""); address.Enabled = false; back.Enabled = false; forward.Enabled = false; reload.Enabled = false; home.Enabled = false; downloads.Enabled = false;
            Text = "加载标签页 — agbrowser";
            _ = EnsureTab(tab);
        }
        else UpdateTab(tab, forceAddress: true);
    }

    private void ApplyBackgroundMemoryPolicy()
    {
        if (IsDisposed || Disposing || browserProcessFailed || recoveryInProgress) return;
        int remaining = tabs.Count, checkedViews = 0;
        while (remaining-- > 0 && checkedViews < BackgroundMemoryBatchSize && tabs.Count > 0)
        {
            if (backgroundMemoryCursor >= tabs.Count) backgroundMemoryCursor = 0;
            var tab = tabs[backgroundMemoryCursor++];
            if ((tab == active && !memoryWindowMinimized) || tab.LowMemoryApplied || tab.MemoryTargetUnavailable || tab.IsNavigating || tab.Downloads.Count != 0 ||
                Stopwatch.GetElapsedTime(tab.InactiveSince) < BackgroundMemoryDelay || tab.View.IsDisposed || tab.View.CoreWebView2 is not { } core) continue;
            checkedViews++;
            try { if (!core.IsDocumentPlayingAudio) SetTabMemoryTarget(tab, low: true); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException)
            { tab.MemoryTargetUnavailable = true; }
        }
    }

    private static void SetTabMemoryTarget(BrowserTab tab, bool low)
    {
        if (tab.View.IsDisposed || tab.View.CoreWebView2 is not { } core) return;
        try
        {
            core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            tab.LowMemoryApplied = low;
            if (low) tab.UsedMemoryTarget = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { if (low) tab.MemoryTargetUnavailable = true; }
    }

    private void CloseTab(BrowserTab tab, bool createReplacement = true)
    {
        if (recoveryInProgress) return;
        if (!tabs.Contains(tab) || tab.ClosePending) return;
        if (createReplacement && !browserProcessFailed && !tab.Failed && !tab.IsNewTab && tab.View.CoreWebView2 is not null)
        {
            _ = RequestCloseTab(tab);
            return;
        }
        FinishCloseTab(tab, createReplacement);
    }

    private void FinishCloseTab(BrowserTab tab, bool createReplacement = true, BrowserTab? preferredTab = null, ClosedTab? snapshot = null)
    {
        if (recoveryInProgress) return;
        var index = tabs.IndexOf(tab); if (index < 0) return;
        if (createReplacement)
        {
            recentlyClosed.Add(snapshot is null ? new ClosedTab(tab.Url, tab.Title, SameIconPage(tab.IconSource, tab.Url) ? tab.IconData : null, index) : snapshot with { Index = index });
            if (recentlyClosed.Count > 25) recentlyClosed.RemoveAt(0);
        }
        tabs.RemoveAt(index); rail.Controls.Remove(tab.Button);
        tab.Button.Dispose();
        if (tab.Downloads.Count == 0) { content.Controls.Remove(tab.View); tab.View.Dispose(); }
        else
        {
            tab.DetachedForDownloads = true; tab.View.Visible = false; retainedDownloadTabs.Add(tab);
            // Keep the source controller alive for downloads without asking a
            // closed page to navigate: that could raise a hidden beforeunload
            // dialog. Suspension is best effort; the controller remains
            // available until all downloads have finished.
            // WebView2 advises against mixing its memory target and suspend
            // APIs on one controller. Keep an already managed page running
            // at its current target until the downloads end.
            if (!tab.UsedMemoryTarget) _ = SuspendDetachedDownloadTab(tab.View);
        }
        if (active == tab)
        {
            active = null;
            if (tabs.Count > 0) SelectTab(preferredTab is not null && tabs.Contains(preferredTab) ? preferredTab : tab.ScriptOpened && tab.Opener is not null && tabs.Contains(tab.Opener) ? tab.Opener : tabs[Math.Min(index, tabs.Count - 1)]);
            else if (createReplacement) _ = AddTab("about:blank", focusAddress: true);
            else { address.Enabled = false; reload.Enabled = false; home.Enabled = false; downloads.Enabled = false; }
        }
        ScheduleSessionSave();
    }

    private static async Task SuspendDetachedDownloadTab(WebView2 view)
    {
        try { if (view.CoreWebView2 is { } core) await core.TrySuspendAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
    }

    private void TrackDownload(BrowserTab tab, WebView2 view, CoreWebView2DownloadOperation download)
    {
        if (!tabs.Contains(tab) || !ReferenceEquals(tab.View, view) || tab.Downloads.ContainsKey(download)) return;
        EventHandler<object>? changed = null;
        changed = (_, _) => {
            bool finished;
            try { finished = download.State == CoreWebView2DownloadState.Completed || (download.State == CoreWebView2DownloadState.Interrupted && !download.CanResume); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { finished = true; }
            if (!finished) return;
            try { download.StateChanged -= changed; } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
            tab.Downloads.Remove(download);
            // Let WebView2 finish dispatching the state change before tearing
            // down its controller; other listeners still need the result.
            if (tab.DetachedForDownloads && tab.Downloads.Count == 0 && IsHandleCreated && !IsDisposed && !Disposing)
                try { BeginInvoke(() => ReleaseDownloadTab(tab)); } catch (InvalidOperationException) { }
        };
        tab.Downloads.Add(download, changed);
        download.StateChanged += changed;
    }

    private void ReleaseDownloadTab(BrowserTab tab, bool force = false)
    {
        if (!tab.DetachedForDownloads || (!force && tab.Downloads.Count > 0)) return;
        foreach (var pair in tab.Downloads)
            try { pair.Key.StateChanged -= pair.Value; } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
        tab.Downloads.Clear(); tab.DetachedForDownloads = false; retainedDownloadTabs.Remove(tab);
        if (!tab.View.IsDisposed) { content.Controls.Remove(tab.View); tab.View.Dispose(); }
    }
    private async Task ReopenClosedTab()
    {
        if (recentlyClosed.Count == 0 || browserProcessFailed || recoveryInProgress || environment is null) return;
        var closed = recentlyClosed[^1]; recentlyClosed.RemoveAt(recentlyClosed.Count - 1);
        var tab = await AddTab(closed.Url);
        if (tab is null)
        {
            if (!IsDisposed && !Disposing) recentlyClosed.Add(closed);
            return;
        }
        int currentIndex = tabs.IndexOf(tab);
        if (currentIndex < 0) return;
        int target = Math.Clamp(closed.Index, 0, tabs.Count - 1);
        tabs.RemoveAt(currentIndex); tabs.Insert(target, tab);
        rail.Controls.SetChildIndex(tab.Button, target); rail.ScrollControlIntoView(tab.Button);
        if (string.IsNullOrEmpty(tab.Title) && tab.IconData is null) RestoreTabAppearance(tab, closed.Title, closed.IconData);
        ScheduleSessionSave();
    }
    private void HandleProcessFailure(BrowserTab tab, CoreWebView2ProcessFailedKind kind)
    {
        if (!tabs.Contains(tab))
        {
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited && retainedDownloadTabs.Contains(tab) && active is { } current)
                HandleProcessFailure(current, kind);
            return;
        }
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited) tab.Failed = true;
        else if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            if (browserProcessFailed) return;
            browserProcessFailed = true;
            if (IsHandleCreated && !IsDisposed && !Disposing)
                try { BeginInvoke(() => { foreach (var retained in retainedDownloadTabs.ToArray()) ReleaseDownloadTab(retained, force: true); }); } catch (InvalidOperationException) { }
            favoriteAdd.Enabled = false;
            sessionTimer.Stop();
            SaveSession();
        }
        else return; // WebView2 recovers helper processes; a slow page may become responsive again.
        if (active == tab || browserProcessFailed) ShowRecovery();
    }
    private void ShowRecovery()
    {
        if (browserProcessFailed)
        {
            recoveryMessage.Text = recoveryUnavailable ? "无法恢复浏览器内核。请关闭并重新打开 agbrowser，以尝试恢复已保存的标签页。" : "浏览器内核已退出，可以重新启动并恢复标签页。";
            recoveryButton.Text = "恢复浏览器"; recoveryButton.AccessibleName = "恢复浏览器";
            recoveryButton.Visible = true; recoveryButton.Enabled = browserResourcesReleased && !recoveryInProgress && !recoveryUnavailable;
            address.Enabled = false; back.Enabled = false; forward.Enabled = false; reload.Enabled = false; home.Enabled = false; downloads.Enabled = false;
        }
        else
        {
            recoveryMessage.Text = "此页面意外退出，可以重新加载。";
            recoveryButton.Text = "重新加载"; recoveryButton.AccessibleName = "重新加载出错的页面";
            recoveryButton.Visible = true; recoveryButton.Enabled = true;
            SetDisplayedAddress(active?.Url ?? ""); address.Enabled = true;
            back.Enabled = false; forward.Enabled = false; reload.Enabled = true;
        }
        recovery.Visible = true; recovery.BringToFront();
    }
    private async Task RecoverBrowser()
    {
        if (!browserProcessFailed || !browserResourcesReleased || recoveryInProgress || recoveryUnavailable || active is null) return;
        recoveryInProgress = true;
        recoveryButton.Enabled = false;
        recoveryMessage.Text = "正在恢复浏览器和标签页…";
        var selected = active;
        try
        {
            foreach (var tab in tabs)
            {
                foreach (var pair in tab.Downloads)
                    try { pair.Key.StateChanged -= pair.Value; } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { }
                tab.Downloads.Clear();
                var old = tab.View;
                content.Controls.Remove(old); old.Dispose();
                tab.View = new WebView2 { Dock = DockStyle.Fill, Visible = false };
                tab.Loading = null; tab.Navigate = true; tab.Failed = false; tab.IconGeneration++;
                tab.LowMemoryApplied = false; tab.UsedMemoryTarget = false; tab.MemoryTargetUnavailable = false; tab.InactiveSince = Stopwatch.GetTimestamp();
                content.Controls.Add(tab.View);
            }
            selected.View.Visible = true; selected.View.BringToFront();
            recovery.BringToFront();
            browserProcessFailed = false;
            selected.Loading = InitializeTab(selected, preserveOnFailure: true);
            if (!await selected.Loading || IsDisposed || Disposing || browserProcessFailed) throw new InvalidOperationException("WebView2 初始化失败");
            browserResourcesReleased = false;
            recovery.Visible = false;
            if (selected.IsNewTab) { selected.View.Visible = false; newTabPage.Visible = true; newTabPage.BringToFront(); }
            UpdateFavoriteAvailability();
            ScheduleSessionSave();
        }
        catch (Exception)
        {
            if (IsDisposed || Disposing) return;
            browserProcessFailed = true;
            recoveryUnavailable = true;
            SaveSession();
            ShowRecovery();
        }
        finally { recoveryInProgress = false; if (!IsDisposed && !Disposing) UpdateFavoriteAvailability(); }
        while (!IsDisposed && !Disposing && !browserProcessFailed && pendingExternalUrls.Count > 0)
        {
            string url = pendingExternalUrls[0]; pendingExternalUrls.RemoveAt(0);
            if (await AddTab(Normalize(url)) is null) { pendingExternalUrls.Insert(0, url); break; }
        }
        ScheduleSessionSave();
    }
    private void RetryFailedPage()
    {
        var tab = active;
        if (browserProcessFailed || tab?.Failed != true) return;
        try { tab.View.CoreWebView2.Reload(); tab.Failed = false; recovery.Visible = false; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { recoveryMessage.Text = "无法重新加载页面：" + ex.Message; }
    }
    private async void Navigate(string input)
    {
        if (browserProcessFailed || recoveryInProgress || active is not { } tab) return;
        DismissAddressSuggestions();
        string target = Normalize(input);
        if (tab.IsNewTab && string.Equals(target, "about:blank", StringComparison.OrdinalIgnoreCase)) { SetDisplayedAddress(""); return; }
        bool wasNewTab = tab.IsNewTab;
        tab.IsNewTab = false; tab.Url = target; tab.Navigate = true; tab.Failed = false;
        newTabPage.Visible = false; tab.View.Visible = true; tab.View.BringToFront();
        if (wasNewTab)
        {
            tab.Button.DisplayTitle = target; tab.Button.AccessibleName = target; tooltip.SetToolTip(tab.Button, target);
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Host.Length > 0) tab.Button.Fallback = uri.Host[..1].ToUpperInvariant();
            tab.Button.Invalidate();
        }
        SetDisplayedAddress(target);
        recovery.Visible = false;
        ScheduleSessionSave();
        try
        {
            if (tab.View.CoreWebView2 is { } core) core.Navigate(target);
            else
            {
                loadingIndicator.Visible = true; tab.Button.Loading = true; Text = "加载标签页 — agbrowser";
                await EnsureTab(tab);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (!IsDisposed && !Disposing && tabs.Contains(tab)) ChromeMessage.Show(this, "无法打开网址：" + ex.Message, "agbrowser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private static string Normalize(string input)
    {
        var text = input.Trim(); if (text.Length == 0) return "about:blank";
        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile)) return uri.AbsoluteUri;
        if (!text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith('[')))
        {
            if (Uri.TryCreate("https://" + text, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host))
            {
                if (DefaultsToHttp(uri) && Uri.TryCreate("http://" + text, UriKind.Absolute, out var local)) return local.AbsoluteUri;
                return uri.AbsoluteUri;
            }
        }
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }
    private static bool DefaultsToHttp(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return address.IsIPv6LinkLocal || (address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc);
    }
    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        // WebView2 forwards accelerator keys as WinForms key events, but its
        // browser process is blocked while the event is being dispatched.
        // Queue work (especially calls back into WebView2) until it returns.
        // KeyPreview and the child can see the same event; handle it once.
        if (e.Handled || !CanHandleShortcut(e.KeyData)) return;
        var keys = e.KeyData;
        e.SuppressKeyPress = true;
        if (!IsDisposed && IsHandleCreated)
            try { BeginInvoke(() => { if (!IsDisposed) HandleShortcut(keys); }); }
            catch (InvalidOperationException) { }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (CanHandleShortcut(keyData))
        {
            if (!IsDisposed && IsHandleCreated)
                try { BeginInvoke(() => { if (!IsDisposed) HandleShortcut(keyData); }); } catch (InvalidOperationException) { }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private bool CanHandleShortcut(Keys keys) =>
        keys == (Keys.Control | Keys.T) ||
        (keys == (Keys.Control | Keys.Shift | Keys.T) && recentlyClosed.Count > 0 && !browserProcessFailed && !recoveryInProgress) ||
        keys == (Keys.Control | Keys.L) ||
        ((keys == (Keys.Control | Keys.W) || keys == (Keys.Control | Keys.F4)) && active != null) ||
        (keys == (Keys.Control | Keys.J) && !browserProcessFailed && active != null) ||
        (keys == (Keys.Control | Keys.D) && favoriteAdd.Enabled) ||
        (keys == (Keys.Control | Keys.F) && CanFindInPage() && active?.View.ContainsFocus == false) ||
        (active != null && !browserProcessFailed &&
            (keys == (Keys.Alt | Keys.Left) || keys == (Keys.Alt | Keys.Right) || keys == (Keys.Alt | Keys.Home) ||
             keys == (Keys.Control | Keys.R) || keys == Keys.F5 || keys == (Keys.Control | Keys.F5) || keys == (Keys.Control | Keys.Shift | Keys.R) ||
             keys == Keys.F12 || keys == (Keys.Control | Keys.Shift | Keys.I) ||
             (keys == Keys.Escape && active.IsNavigating && !address.Focused && !suggestionPopup.Visible))) ||
        (TryGetTabShortcutIndex(keys, out _) && tabs.Count > 0 && !recoveryInProgress) ||
        ((keys == (Keys.Control | Keys.Tab) || keys == (Keys.Control | Keys.Shift | Keys.Tab) || keys == (Keys.Control | Keys.PageUp) || keys == (Keys.Control | Keys.PageDown)) && tabs.Count > 1 && active != null);
    private static bool TryGetTabShortcutIndex(Keys keys, out int index)
    {
        var key = keys & Keys.KeyCode;
        if ((keys & Keys.Modifiers) == Keys.Control && key >= Keys.D1 && key <= Keys.D9)
        {
            index = key - Keys.D1;
            return true;
        }
        index = -1;
        return false;
    }
    private bool HandleShortcut(Keys keys)
    {
        if (keys == (Keys.Control | Keys.T)) { _ = AddTab("about:blank", focusAddress: true); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.T) && recentlyClosed.Count > 0 && !browserProcessFailed && !recoveryInProgress) { _ = ReopenClosedTab(); return true; }
        if ((keys == (Keys.Control | Keys.W) || keys == (Keys.Control | Keys.F4)) && active != null) { CloseTab(active); return true; }
        if (keys == (Keys.Control | Keys.J) && active != null) { _ = OpenDownloads(); return true; }
        if (keys == (Keys.Control | Keys.D) && favoriteAdd.Enabled) { _ = AddCurrentFavorite(); return true; }
        if (keys == (Keys.Control | Keys.L)) { address.Focus(); address.SelectAll(); return true; }
        if (keys == (Keys.Control | Keys.F) && CanFindInPage()) { _ = OpenPageFind(); return true; }
        if (keys == (Keys.Alt | Keys.Home) && active != null) { Navigate("about:blank"); return true; }
        if ((keys == Keys.F12 || keys == (Keys.Control | Keys.Shift | Keys.I)) && active != null) { _ = OpenDevTools(); return true; }
        if (active?.IsNewTab == true && (keys == (Keys.Control | Keys.R) || keys == Keys.F5 || keys == (Keys.Control | Keys.F5) || keys == (Keys.Control | Keys.Shift | Keys.R))) return true;
        if (active?.View.CoreWebView2 is { } browser)
        {
            if (keys == (Keys.Alt | Keys.Left)) { if (browser.CanGoBack) browser.GoBack(); return true; }
            if (keys == (Keys.Alt | Keys.Right)) { if (browser.CanGoForward) browser.GoForward(); return true; }
            if (keys == (Keys.Control | Keys.R) || keys == Keys.F5) { if (active.Failed) RetryFailedPage(); else browser.Reload(); return true; }
            if (keys == (Keys.Control | Keys.F5) || keys == (Keys.Control | Keys.Shift | Keys.R)) { _ = browser.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"); return true; }
            if (keys == Keys.Escape && active.IsNavigating) { browser.Stop(); return true; }
        }
        if (TryGetTabShortcutIndex(keys, out int index) && tabs.Count > 0 && !recoveryInProgress)
        {
            if (index == 8) index = tabs.Count - 1;
            if (index < tabs.Count) SelectTab(tabs[index]);
            return true;
        }
        if ((keys == (Keys.Control | Keys.Tab) || keys == (Keys.Control | Keys.Shift | Keys.Tab) || keys == (Keys.Control | Keys.PageUp) || keys == (Keys.Control | Keys.PageDown)) && tabs.Count > 1 && active != null)
        {
            int delta = (keys & Keys.Shift) != 0 || keys == (Keys.Control | Keys.PageUp) ? tabs.Count - 1 : 1;
            SelectTab(tabs[(tabs.IndexOf(active) + delta) % tabs.Count]); return true;
        }
        return false;
    }
    private string SessionPath => Path.Combine(dataDir, "session.json");
    private sealed record SessionState(string[] Urls, int ActiveIndex, string?[] Titles, string?[] Icons)
    {
        public static SessionState Empty() => new([], 0, [], []);
    }
    private SessionState LoadSession()
    {
        try
        {
            using var stream = new FileStream(SessionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) &&
                (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1))
                throw new JsonException("Unsupported session version");
            var values = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("urls");
            if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                throw new JsonException("Invalid session URLs");
            var urls = values.EnumerateArray().Select(x => x.GetString()!).ToArray();
            int index = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("activeIndex", out var activeIndex) && activeIndex.TryGetInt32(out int n) ? n : 0;
            return new SessionState(urls, index, ReadAppearance(root, "titles", urls.Length, 1024), ReadAppearance(root, "icons", urls.Length, 32768));
        }
        catch (FileNotFoundException) { return SessionState.Empty(); }
        catch (DirectoryNotFoundException) { return SessionState.Empty(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Keep damaged or temporarily unavailable data for recovery instead
            // of silently replacing it with an empty session at shutdown.
            sessionReadable = false;
            SetSessionWarning("无法读取原有会话；原文件已保留，本次标签更改不会保存。");
            return SessionState.Empty();
        }
    }
    private static string?[] ReadAppearance(JsonElement root, string property, int count, int limit)
    {
        var result = new string?[count];
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return result;
        int i = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (i >= count) break;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= limit) result[i] = text;
            i++;
        }
        return result;
    }
    private void RestoreTabAppearance(BrowserTab tab, string? title, string? iconData)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            tab.Title = title; tab.Button.AccessibleName = title; tab.Button.DisplayTitle = title; tab.Button.Invalidate(); tooltip.SetToolTip(tab.Button, title);
            tab.Button.Fallback = title[..1].ToUpperInvariant();
        }
        if (iconData is null) return;
        try
        {
            byte[] bytes = Convert.FromBase64String(iconData);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return;
            uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is 0 or > 64 || height is 0 or > 64) return;
            using var stream = new MemoryStream(bytes, writable: false);
            using var icon = Image.FromStream(stream);
            tab.Button.Image = new Bitmap(icon, new Size(24, 24));
            tab.IconData = iconData;
            tab.IconSource = tab.Url;
            ApplyLiveFavoriteIcon(tab.Url, bytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
    }
    private void ScheduleSessionSave()
    {
        if (!sessionRestored || !sessionReadable || IsDisposed || Disposing) return;
        sessionTimer.Stop();
        sessionTimer.Start();
    }
    private void SaveSession()
    {
        if (!sessionRestored || !sessionReadable) return; // Preserve an unreadable or incompletely restored session.
        try
        {
            Directory.CreateDirectory(dataDir);
            string temporary = SessionPath + ".tmp";
            var urls = tabs.Select(t => t.Url).Concat(pendingExternalUrls.Select(Normalize)).ToArray();
            int index = pendingExternalUrls.Count > 0 ? urls.Length - 1 : active is null ? 0 : Math.Max(0, tabs.IndexOf(active));
            var titles = tabs.Select(t => t.Title is { Length: <= 1024 } ? t.Title : null).Concat(pendingExternalUrls.Select(_ => (string?)null)).ToArray();
            var icons = tabs.Select(t => SameIconPage(t.IconSource, t.Url) ? t.IconData : null).Concat(pendingExternalUrls.Select(_ => (string?)null)).ToArray();
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, urls, activeIndex = index, titles, icons }));
            File.Move(temporary, SessionPath, true);
            SetSessionWarning(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetSessionWarning("无法保存当前标签，请检查磁盘空间和数据目录权限。"); }
    }
    private void SetSessionWarning(string? message)
    {
        sessionWarning.Text = message ?? "";
        sessionWarningRow.Height = message is null ? 0 : LogicalToDeviceUnits(32);
        sessionWarning.Visible = message is not null;
    }
    private sealed record ClosedTab(string Url, string? Title, string? IconData, int Index);
    private static bool SameIconPage(string? first, string? second) =>
        first is not null && second is not null && (string.Equals(first, second, StringComparison.Ordinal) ||
            FavoriteIcons.TryPageKey(first, out var left) && FavoriteIcons.TryPageKey(second, out var right) && left == right);
    private sealed class BrowserTab(WebView2 view, RailTabButton button, string url, bool navigate)
    {
        public WebView2 View { get; set; } = view;
        public RailTabButton Button { get; } = button;
        public string Url { get; set; } = url;
        public string? Title { get; set; }
        public string? IconData { get; set; }
        public string? IconSource { get; set; }
        public bool Navigate { get; set; } = navigate;
        public bool ScriptOpened { get; set; }
        public bool IsNewTab { get; set; }
        public BrowserTab? Opener { get; set; }
        public string? TopLevelUrl { get; set; }
        public string? HistoryUrl { get; set; }
        public long IconGeneration { get; set; }
        public bool Failed { get; set; }
        public bool IsNavigating { get; set; }
        public bool DetachedForDownloads { get; set; }
        public bool ClosePending { get; set; }
        public long InactiveSince { get; set; } = Stopwatch.GetTimestamp();
        public bool LowMemoryApplied { get; set; }
        public bool UsedMemoryTarget { get; set; }
        public bool MemoryTargetUnavailable { get; set; }
        public Dictionary<CoreWebView2DownloadOperation, EventHandler<object>> Downloads { get; } = [];
        public ulong NavigationId { get; set; }
        public Task<bool>? Loading { get; set; }
    }
}
