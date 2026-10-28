using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using AgBrowser.Desktop;

internal static class ChromeDialogTests
{
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr deviceMode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCueBanner(IntPtr window, int message, StringBuilder buffer, IntPtr length);

    public static void Run(string directory)
    {
        var done = new ManualResetEventSlim(); Exception? failure = null;
        var thread = new Thread(() => {
            try
            {
                IntPtr desktop = CreateDesktop("agbrowser-dialog-test-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
                RunOnDesktop(directory);
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!done.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("dialog integration test did not complete");
        if (failure is not null) throw new InvalidOperationException("dialog integration test failed", failure);
    }

    private static void RunOnDesktop(string directory)
    {
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(directory, "no-edge"));
        Program.InitializeTestApplication();
        new BrowserNetworkSettings("http://127.0.0.1:8888", CaptureLoopback: true, AgentDebug: false).Save(directory);
        using var browser = new BrowserWindow([]);
        using var owner = new Form { ShowInTaskbar = false }; owner.Show();
        var factory = typeof(BrowserWindow).GetMethod("CreateBrowserNetworkDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;

        using (var dialog = (ChromeDialog)factory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                var controls = Descendants(dialog).ToArray();
                var input = controls.OfType<TextBox>().Single(item => item.AccessibleName == "浏览器代理地址");
                var loopback = controls.OfType<ChromeToggle>().Single(item => item.AccessibleName == "通过代理抓取本机流量");
                var debug = controls.OfType<ChromeToggle>().Single(item => item.AccessibleName == "开启本机 Agent 调试端口");
                Assert(input.Text == "http://127.0.0.1:8888" && loopback.Checked && !debug.Checked, "saved network options are shown");
                var cue = new StringBuilder(256);
                Assert(GetCueBanner(input.Handle, 0x1502, cue, new IntPtr(cue.Capacity)) != IntPtr.Zero && cue.ToString().Contains("127.0.0.1:8888"), "proxy input has a native cue banner");
                input.Text = ""; SavePreview(dialog, "network-dialog.png");
                input.Text = "http://127.0.0.1:9999";
                SendMessage(loopback.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                debug.Select();
                SendMessage(debug.Handle, 0x0100, new IntPtr((int)Keys.Space), IntPtr.Zero);
                SendMessage(debug.Handle, 0x0101, new IntPtr((int)Keys.Space), IntPtr.Zero);
                Assert(!loopback.Checked, "native toggle responds to a click");
                Assert(debug.Checked, "focused native toggle responds to Space");
                Assert(DialogKey(dialog, Keys.Enter), "Enter accepts the dialog");
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.OK, "save confirms the dialog");
        }
        var saved = BrowserNetworkSettings.Load(directory);
        Assert(saved.ProxyUrl == "http://127.0.0.1:9999" && !saved.CaptureLoopback && saved.AgentDebug, "save persists the changed network options");
        using (var dialog = (ChromeDialog)factory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "浏览器代理地址").Text = "http://127.0.0.1:7777";
                Assert(DialogKey(dialog, Keys.Escape), "Escape cancels the dialog");
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.Cancel, "cancel closes the dialog");
        }
        Assert(BrowserNetworkSettings.Load(directory).ProxyUrl == "http://127.0.0.1:9999", "cancel leaves saved network options unchanged");
        using (var dialog = (ChromeDialog)factory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "浏览器代理地址").Text = "ftp://invalid";
                bool sawError = false;
                using var closeError = new System.Windows.Forms.Timer { Interval = 30 };
                closeError.Tick += (_, _) => {
                    var error = Application.OpenForms.OfType<ChromeDialog>().FirstOrDefault(item => item.Text == "设置未保存");
                    if (error is null) return;
                    sawError = true; closeError.Stop(); ((Button)error.AcceptButton!).PerformClick();
                };
                closeError.Start(); ((Button)dialog.AcceptButton!).PerformClick();
                Assert(sawError && dialog.Visible, "invalid proxy displays an error without closing settings");
                ((Button)dialog.CancelButton!).PerformClick();
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.Cancel, "invalid settings can be cancelled");
        }
        Assert(BrowserNetworkSettings.Load(directory).ProxyUrl == "http://127.0.0.1:9999", "invalid proxy does not replace saved settings");
        Assert(BrowserDownloadSettings.TryLoad(directory, out var defaultDownload, out _) && defaultDownload.ParallelRequests is null &&
            defaultDownload.EffectiveRequests == Environment.ProcessorCount * 2, "download parallelism defaults to twice the logical processor count");
        Assert(new BrowserDownloadSettings(1).BrowserArguments().Contains("--disable-features=ParallelDownloading", StringComparison.Ordinal) &&
            new BrowserDownloadSettings(4).BrowserArguments().Contains("request_count/4", StringComparison.Ordinal), "single and parallel settings produce the correct browser options");
        var downloadSettingsFactory = typeof(BrowserWindow).GetMethod("CreateDownloadSettingsDialog", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (var dialog = (ChromeDialog)downloadSettingsFactory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                var input = Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "每个下载的最大并行请求数");
                Assert(input.Text == "" && input.PlaceholderText.Contains("CPU", StringComparison.Ordinal), "automatic download setting is shown");
                input.Text = "4";
                Assert(DialogKey(dialog, Keys.Enter), "Enter saves download settings");
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.OK, "download settings save");
        }
        Assert(BrowserDownloadSettings.TryLoad(directory, out var savedDownload, out _) && savedDownload.ParallelRequests == 4, "download setting persists");
        using (var dialog = (ChromeDialog)downloadSettingsFactory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "每个下载的最大并行请求数").Text = "";
                Assert(DialogKey(dialog, Keys.Enter), "automatic download setting saves");
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.OK, "automatic mode can be restored");
        }
        Assert(BrowserDownloadSettings.TryLoad(directory, out savedDownload, out _) && savedDownload.ParallelRequests is null, "automatic download setting persists");
        string downloadSettingsPath = Path.Combine(directory, "download-settings.json");
        File.WriteAllText(downloadSettingsPath, "{invalid");
        using (var dialog = (ChromeDialog)downloadSettingsFactory.Invoke(browser, [])!)
        {
            OnShown(dialog, () => {
                Assert(!((Button)dialog.AcceptButton!).Enabled, "unreadable download settings cannot be overwritten");
                ((Button)dialog.CancelButton!).PerformClick();
            });
            Assert(ShowChecked(dialog, owner) == DialogResult.Cancel && File.ReadAllText(downloadSettingsPath) == "{invalid", "unreadable download settings are preserved");
        }
        File.Delete(downloadSettingsPath);
        using (var message = ChromeMessage.Create("确认安装更新？", "确认更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question))
        {
            OnShown(message, () => {
                SavePreview(message, "message-dialog.png");
                ((Button)message.CancelButton!).PerformClick();
            });
            Assert(ShowChecked(message, owner) == DialogResult.No, "confirmation preserves the negative response");
        }
        var downloadPromptFactory = typeof(BrowserWindow).GetMethod("CreateDownloadCloseDialog", BindingFlags.NonPublic | BindingFlags.Static)!;
        using (var warning = (ChromeDialog)downloadPromptFactory.Invoke(null, [2])!)
        {
            OnShown(warning, () => {
                Assert(Descendants(warning).OfType<Label>().Any(label => label.Text.Contains("2 个下载", StringComparison.Ordinal)), "download warning shows the pending count");
                Assert(DialogKey(warning, Keys.Enter), "Enter chooses the safe download action");
            });
            Assert(ShowChecked(warning, owner) == DialogResult.No, "download warning keeps the browser open by default");
        }
        using (var warning = (ChromeDialog)downloadPromptFactory.Invoke(null, [1])!)
        {
            OnShown(warning, () => Descendants(warning).OfType<Button>().Single(button => button.Text == "关闭浏览器").PerformClick());
            Assert(ShowChecked(warning, owner) == DialogResult.Yes, "download warning allows an explicit close");
        }
        using (var sizeable = new ChromeDialog("可调整大小", new Size(620, 480), resizable: true))
        {
            OnShown(sizeable, () => {
                int original = sizeable.Body.ClientSize.Width;
                sizeable.Size = new Size(sizeable.Width + 120, sizeable.Height + 80); sizeable.PerformLayout();
                Assert(sizeable.Body.ClientSize.Width > original, "resizable dialog body follows its window");
                Point edge = sizeable.PointToScreen(new Point(1, sizeable.ClientSize.Height / 2));
                int coordinates = unchecked((ushort)edge.X | ((ushort)edge.Y << 16));
                Assert(SendMessage(sizeable.Handle, 0x0084, IntPtr.Zero, new IntPtr(coordinates)) == new IntPtr(10), "left border is a resize handle");
                sizeable.Close();
            });
            ShowChecked(sizeable, owner);
        }
        var aiFactory = typeof(BrowserWindow).GetMethod("CreateAiMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ai = ((ChromeContextMenu Menu, TextBox Search))aiFactory.Invoke(browser, [])!;
        using (ai.Menu)
        {
            ai.Menu.Show(owner, new Point(10, 10));
            var cue = new StringBuilder(256);
            Assert(GetCueBanner(ai.Search.Handle, 0x1502, cue, new IntPtr(cue.Capacity)) != IntPtr.Zero && cue.ToString().Contains("AI"), "AI search has a native cue banner");
            ai.Search.Text = "Gemini";
            var entries = ai.Menu.Items.OfType<ToolStripMenuItem>().ToArray();
            Assert(entries.Any(item => item.Text == "Gemini" && item.Visible), "AI search retains matching entry");
            Assert(entries.Any(item => item.Text?.StartsWith("ChatGPT", StringComparison.Ordinal) == true && !item.Visible), "AI search hides nonmatching entries");
            Assert(ai.Menu.Region != null, "menu uses rounded window geometry");
            ai.Search.Text = ""; ai.Menu.PerformLayout();
            Assert(entries.Count(item => item.Available) >= 9, "clearing AI search restores entries");
            SavePreview(ai.Menu, "ai-menu.png");
            ai.Menu.Close();
        }
        using (var menu = new ChromeContextMenu())
        {
            var checkedItem = new ToolStripMenuItem("本网站广告拦截") { Checked = true };
            menu.Items.Add(checkedItem); menu.Show(owner, new Point(10, 10));
            Assert(checkedItem.Checked && checkedItem.Visible, "checked browser menu option remains visible");
            SavePreview(menu, "browser-menu.png"); menu.Close();
        }
        var createFavorite = typeof(BrowserWindow).GetMethod("CreateFavoriteItem", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var node = new FavoriteNode("folder", "示例收藏夹", null, [new FavoriteNode("link", "示例网址", "https://example.test/", [])]);
        using (var strip = new ToolStrip())
        {
            owner.Controls.Add(strip);
            var folder = (ToolStripDropDownButton)createFavorite.Invoke(browser, [node, false, 0L])!;
            strip.Items.Add(folder); folder.ShowDropDown();
            Assert(folder.Image != null && folder.DropDownItems.Count == 1 && folder.DropDownItems[0].Image != null, "favorite folder and site icons remain visible");
            folder.HideDropDown();
        }
        var mobile = new FavoriteNode("mobile", "移动设备收藏", null, [new FavoriteNode("phone", "手机收藏", "https://phone.example.test/", [])]);
        var managed = new FavoriteNode("managed", "组织收藏", null, [new FavoriteNode("managed-link", "只读网址", "https://managed.example.test/", [], false)], false);
        var applyFavorites = typeof(BrowserWindow).GetMethod("ApplyFavorites", BindingFlags.NonPublic | BindingFlags.Instance)!;
        applyFavorites.Invoke(browser, [new FavoriteSnapshot("Edge", [], null, "1", AdditionalRoots: [mobile, managed])]);
        var favorites = (ToolStrip)typeof(BrowserWindow).GetField("favoritesBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!;
        var mobileButton = favorites.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "移动设备收藏");
        mobileButton.ShowDropDown();
        Assert(mobileButton.DropDownItems.OfType<ToolStripButton>().Any(item => item.Text == "手机收藏"), "additional roots appear in the favorites bar");
        var pageIndex = typeof(BrowserWindow).GetField("favoritePageIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
        PumpUntil(() => ((Dictionary<string, List<string>>)pageIndex.GetValue(browser)!).ContainsKey("https://phone.example.test/"), "favorite icon index");
        byte[] liveIcon;
        using (var image = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(image))
        using (var stream = new MemoryStream())
        { graphics.Clear(Color.Red); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png); liveIcon = stream.ToArray(); }
        var updateIcon = typeof(BrowserWindow).GetMethod("ApplyLiveFavoriteIcon", BindingFlags.NonPublic | BindingFlags.Instance)!;
        updateIcon.Invoke(browser, ["https://phone.example.test/#fragment", liveIcon]);
        var mobileLink = mobileButton.DropDownItems.OfType<ToolStripButton>().Single(item => item.Text == "手机收藏");
        Assert(mobileLink.Image is Bitmap painted && painted.GetPixel(8, 8).R > 200, "page favicon updates an open favorite immediately");
        var savedIcons = (FavoritePageIconCache)typeof(BrowserWindow).GetField("favoritePageIconCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!;
        savedIcons.Save();
        Assert(new FavoritePageIconCache(directory).Snapshot().TryGetValue(FavoritePageIconCache.HashPageKey("https://phone.example.test/"), out var restoredIcon) && restoredIcon.SequenceEqual(liveIcon),
            "page favicon is available after restarting the browser");
        using (var restartedIcons = new BrowserWindow([]))
        {
            _ = restartedIcons.Handle;
            applyFavorites.Invoke(restartedIcons, [new FavoriteSnapshot("Edge", [], null, "1", AdditionalRoots: [mobile, managed])]);
            var restartedBar = (ToolStrip)typeof(BrowserWindow).GetField("favoritesBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restartedIcons)!;
            var restartedFolder = restartedBar.Items.OfType<ToolStripDropDownButton>().Single(item => item.Text == "移动设备收藏");
            restartedFolder.ShowDropDown();
            PumpUntil(() => restartedFolder.DropDownItems.OfType<ToolStripButton>().FirstOrDefault(item => item.Text == "手机收藏")?.Image is Bitmap image &&
                image.GetPixel(8, 8).R > 200 && image.GetPixel(8, 8).G < 80, "saved favorite icon shown after restart");
            restartedFolder.HideDropDown();
            ((EdgeFavoritesBridge)typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restartedIcons)!).Dispose();
        }
        Assert(FavoriteIcons.SameOrigin("https://phone.example.test/", "https://phone.example.test:443/path") &&
            FavoriteIcons.SameOrigin("HTTPS://PHONE.EXAMPLE.TEST/", "https://phone.example.test/path") &&
            !FavoriteIcons.SameOrigin("https://phone.example.test/", "http://phone.example.test/") &&
            !FavoriteIcons.SameOrigin("https://phone.example.test/", "https://phone.example.test:8443/"),
            "icon reuse respects scheme and port");
        var sameIconPage = typeof(BrowserWindow).GetMethod("SameIconPage", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert((bool)sameIconPage.Invoke(null, ["https://phone.example.test/#one", "https://phone.example.test/#two"])! &&
            !(bool)sameIconPage.Invoke(null, ["https://phone.example.test/old", "https://phone.example.test/new"])!,
            "a tab icon cannot be saved against a different page during navigation");
        mobileButton.HideDropDown();
        var folderMenu = typeof(BrowserWindow).GetMethod("CreateFavoriteFolderMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (var managedMenu = (ContextMenuStrip)folderMenu.Invoke(browser, [managed, true, 1L])!)
            Assert(managedMenu.Items.Count == 1 && !managedMenu.Items[0].Enabled, "managed folder does not expose write actions");
        var linkMenu = typeof(BrowserWindow).GetMethod("CreateFavoriteLinkMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using (var managedLinkMenu = (ContextMenuStrip)linkMenu.Invoke(browser, [managed.Children[0], 1L])!)
            Assert(managedLinkMenu.Items.Count == 1 && managedLinkMenu.Items[0].Text == "编辑标签", "managed link exposes only local tags");
        VerifyProfileSelectionMenu(owner, Path.Combine(directory, "profiles"));
        VerifyFavoriteEditAfterReconnect(browser, directory);
        owner.Close();
        Console.WriteLine("PASS native dialogs and menus: settings, keyboard, confirmations, resizing, AI filtering, favorite icons and reconnect safety");
    }

    private static void VerifyProfileSelectionMenu(Form owner, string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        try
        {
            using var browser = new BrowserWindow([]);
            _ = browser.Handle;
            var bridge = (EdgeFavoritesBridge)typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!;
            try
            {
                bridge.Start();
                string name = "agbrowser-edge-favorites-" + SingleInstance.ProfileHash(directory);
                using var first = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                first.Connect(5000); WriteSnapshot(first, "Work profile", "10000000-0000-4000-8000-000000000001");
                var snapshot = typeof(BrowserWindow).GetField("bridgeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
                PumpUntil(() => snapshot.GetValue(browser) is FavoriteSnapshot current && current.Bar[0].Name == "Work profile", "first profile shown");
                using var second = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                second.Connect(5000); WriteSnapshot(second, "Personal profile", "20000000-0000-4000-8000-000000000002");
                PumpUntil(() => bridge.GetConnections().Count == 2, "both profile choices shown");
                var create = typeof(BrowserWindow).GetMethod("CreateEdgeConnectionsMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
                using var menu = (ChromeContextMenu)create.Invoke(browser, [bridge.GetConnections()])!;
                menu.Show(owner, new Point(10, 10));
                var options = menu.Items.OfType<ToolStripMenuItem>().ToArray();
                Assert(options.Single(item => item.Text?.Contains("Work profile", StringComparison.Ordinal) == true).Checked, "current Edge profile is checked");
                var personal = options.Single(item => item.Text?.Contains("Personal profile", StringComparison.Ordinal) == true);
                Assert(personal.Enabled && !personal.Checked, "other Edge profile is selectable");
                personal.PerformClick();
                PumpUntil(() => snapshot.GetValue(browser) is FavoriteSnapshot current && current.Bar[0].Name == "Personal profile", "selected profile shown");
                Assert(File.Exists(Path.Combine(directory, "edge-favorites-profile.json")), "profile selection is saved");
                second.Dispose();
                PumpUntil(() => snapshot.GetValue(browser) is null && !bridge.Connected, "selected profile disconnected");
                var displayed = typeof(BrowserWindow).GetField("displayedFavorites", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var add = (Button)typeof(BrowserWindow).GetField("favoriteAdd", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!;
                var refresh = typeof(BrowserWindow).GetMethod("RefreshFavorites", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var refreshTask = (Task)refresh.Invoke(browser, [])!;
                PumpUntil(() => refreshTask.IsCompleted, "refresh while selected profile is disconnected");
                refreshTask.GetAwaiter().GetResult();
                Assert(displayed.GetValue(browser) is FavoriteSnapshot cached && cached.Bar[0].Name == "Personal profile" && !add.Enabled,
                    "disconnect keeps the selected profile read-only instead of showing another profile");
                using var restarted = new BrowserWindow([]);
                _ = restarted.Handle;
                var restartRefresh = (Task)refresh.Invoke(restarted, [])!;
                PumpUntil(() => restartRefresh.IsCompleted, "refresh before saved profile reconnects");
                restartRefresh.GetAwaiter().GetResult();
                var bar = (ToolStrip)typeof(BrowserWindow).GetField("favoritesBar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restarted)!;
                Assert(bar.Items.Count == 1 && bar.Items[0].Text == "等待所选 Edge 配置连接",
                    "restart waits for the saved profile instead of reading a different one");
                ((EdgeFavoritesBridge)typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(restarted)!).Dispose();
                menu.Close();
            }
            finally { bridge.Dispose(); }
        }
        finally { Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", Path.GetDirectoryName(directory)); }
    }

    private static void VerifyFavoriteEditAfterReconnect(BrowserWindow browser, string directory)
    {
        Console.WriteLine("Checking favorite edit across a simulated reconnect...");
        var bridge = (EdgeFavoritesBridge)typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!;
        _ = browser.Handle; bridge.Start();
        string name = "agbrowser-edge-favorites-" + SingleInstance.ProfileHash(directory);
        using var first = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        first.Connect(5000); WriteSnapshot(first, "Old profile");
        var snapshotField = typeof(BrowserWindow).GetField("bridgeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        PumpUntil(() => snapshotField.GetValue(browser) is FavoriteSnapshot snapshot && snapshot.Bar.FirstOrDefault()?.Name == "Old profile", "first UI snapshot");
        var original = (FavoriteSnapshot)snapshotField.GetValue(browser)!;
        var edit = typeof(BrowserWindow).GetMethod("EditFavorite", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        NamedPipeClientStream? second = null; Task? reconnect = null; Exception? failure = null; bool warned = false; int stage = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) => {
            try
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException($"reconnect dialog stage {stage}, open forms: {string.Join(", ", Application.OpenForms.Cast<Form>().Select(form => form.Text))}");
                if (stage == 0)
                {
                    var dialog = Application.OpenForms.OfType<ChromeDialog>().FirstOrDefault(item => item.Text == "编辑收藏");
                    if (dialog is null) return;
                    Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "收藏名称").Text = "Edited old profile";
                    first.Dispose();
                    reconnect = Task.Run(() => {
                        second = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        second.Connect(5000); WriteSnapshot(second, "New profile");
                    });
                    stage = 1;
                }
                else if (stage == 1)
                {
                    if (reconnect?.IsFaulted == true) throw reconnect.Exception!;
                    if (reconnect?.IsCompleted != true) return;
                    if (bridge.GetConnections().FirstOrDefault(choice => choice.Preview == "New profile") is { Selected: false } choice)
                        bridge.SelectConnection(choice.Generation);
                    if (snapshotField.GetValue(browser) is not FavoriteSnapshot current || current.Bar.FirstOrDefault()?.Name != "New profile") return;
                    var dialog = Application.OpenForms.OfType<ChromeDialog>().Single(item => item.Text == "编辑收藏");
                    stage = 2;
                    Descendants(dialog).OfType<Button>().Single(item => item.Text == "保存").PerformClick();
                }
                else if (stage == 2)
                {
                    var message = Application.OpenForms.OfType<ChromeDialog>().FirstOrDefault(item => item.Text == "agbrowser" && Descendants(item).OfType<Label>().Any(label => label.Text.Contains("连接已变化", StringComparison.Ordinal)));
                    if (message is null) return;
                    warned = true; stage = 3; timer.Stop(); ((Button)message.AcceptButton!).PerformClick();
                }
            }
            catch (Exception ex)
            {
                failure = ex; timer.Stop();
                foreach (var dialog in Application.OpenForms.OfType<ChromeDialog>().ToArray()) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
            }
        };
        try
        {
            timer.Start();
            var task = (Task)edit.Invoke(browser, [original.Bar[0], original.ConnectionGeneration])!;
            PumpUntil(() => task.IsCompleted || failure is not null, "edit after reconnect");
            task.GetAwaiter().GetResult();
            if (failure is not null) throw new InvalidOperationException("Reconnect fixture failed", failure);
            Assert(warned && stage == 3 && second is not null, "stale edit explains that the Edge connection changed");
            using var reader = new StreamReader(second!, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                string? line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                if (line is not null) throw new InvalidOperationException("Old edit was delivered to the new Edge profile");
            }
            catch (OperationCanceledException) { }
        }
        finally { timer.Stop(); second?.Dispose(); bridge.Dispose(); }
    }

    private static void WriteSnapshot(Stream stream, string name, string? profileId = null)
    {
        string json = JsonSerializer.Serialize(new {
            type = "snapshot", sequence = 1, profileId,
            tree = new[] { new { id = "0", children = new[] { new { id = "1", title = "bar", children = new[] { new { id = "42", title = name, url = "https://example.test/" } } } } } }
        });
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n"); stream.Write(bytes); stream.Flush();
    }

    private static void PumpUntil(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(label); Application.DoEvents(); Thread.Sleep(20); }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void OnShown(ChromeDialog dialog, Action action)
    {
        dialog.Shown += (_, _) => dialog.BeginInvoke(() => {
            try { action(); }
            catch (Exception ex) { dialog.Tag = ex; dialog.DialogResult = DialogResult.Abort; dialog.Close(); }
        });
    }
    private static DialogResult ShowChecked(ChromeDialog dialog, IWin32Window owner)
    {
        DialogResult result = dialog.ShowChromeDialog(owner);
        if (dialog.Tag is Exception ex) throw new InvalidOperationException("dialog action failed", ex);
        return result;
    }
    private static bool DialogKey(ChromeDialog dialog, Keys key) => (bool)typeof(ChromeDialog).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [key])!;
    private static void SavePreview(Control control, string name)
    {
        if (Environment.GetEnvironmentVariable("AGB_TEST_RENDER_OUTPUT") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(directory, name));
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
