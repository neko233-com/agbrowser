using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AgBrowser.Desktop;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    static BrowserWindow? current;
    static int status;
    static string root = Environment.GetEnvironmentVariable("AGB_TEST_RUN_DIR") ?? Path.Combine(Path.GetTempPath(), "agb-integration-" + Guid.NewGuid().ToString("N"));

    internal static void InitializeTestApplication()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--close-page-probe"))
            return ClosePageProbe.Run(root);
        if (args.Contains("--find-shortcut-tests"))
            return FindShortcutTests.Run(root);
        if (args.Contains("--download-tab-close-tests"))
            return DownloadTabCloseTests.Run(root);
        if (args.Contains("--memory-policy-probe"))
            return MemoryPolicyProbe.Run(root);
        if (args.Contains("--edge-connection-tests"))
            return EdgeConnectionRaceTests.Run(root).GetAwaiter().GetResult();
        if (args.Contains("--favorite-icons-real-coverage"))
            return VerifyRealFavoriteIconCoverage().GetAwaiter().GetResult();
        if (args.Contains("--address-search-benchmark"))
        {
            try { AddressSearchBenchmark.Run(root); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--favorites-manager-benchmark"))
        {
            try { FavoritesManagerBenchmark.Run(root); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--hidden-restore-benchmark"))
            return HiddenRestoreBenchmark.Run(root);
        if (args.Contains("--dialog-interaction-tests"))
        {
            try { ChromeDialogTests.Run(root); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (args.Contains("--suggestion-window-tests"))
        {
            try { SuggestionPopupTests.Run(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (Environment.GetEnvironmentVariable("AGB_TEST_STORE_LIVE") is { Length: > 0 } liveId)
            return RunStoreLive(liveId);
        if (args.Contains("--store-tests"))
        {
            try { ChromeStoreTests.Run(root); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (Environment.GetEnvironmentVariable("AGB_TEST_CRX_PATH") is { Length: > 0 } crxPath)
        {
            try {
                string id = Environment.GetEnvironmentVariable("AGB_TEST_EXTENSION_ID") ?? throw new Exception("AGB_TEST_EXTENSION_ID is required");
                var service = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.ChromeStoreExtensions")!;
                var package = service.GetMethod("Verify")!.Invoke(null, [File.ReadAllBytes(crxPath), id])!;
                string directory = (string)package.GetType().GetMethod("ExtractTo")!.Invoke(package, [root])!;
                Console.WriteLine("PASS signed Chrome Store package verified and unpacked in isolation");
                return RunExtensionSmoke(directory);
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (Environment.GetEnvironmentVariable("AGB_TEST_EXTENSION_PATH") is { Length: > 0 } extensionPath)
            return RunExtensionSmoke(extensionPath);
        if (Environment.GetEnvironmentVariable("AGB_TEST_DEBUG_PORT_ONLY") == "1")
        {
            try { VerifyDebugPortCleanupError(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        if (Environment.GetEnvironmentVariable("AGB_STARTUP_BENCH_CHILD") == "1") return RunStartupBenchmarkChild();
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", root);
        // Route fixture ad hosts only to loopback; no test request should reach
        // the real domains, including when a filter is missing.
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-automation --no-proxy-server --host-resolver-rules=\"MAP doubleclick.net 127.0.0.1,MAP *.doubleclick.net 127.0.0.1,MAP *.example.test 127.0.0.1\"");
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Prefixes.Add($"http://localhost:{port}/"); server.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () => {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var context = await server.GetContextAsync(); var path = context.Request.Url!.AbsolutePath;
                    if (path == "/favicon.ico") { context.Response.StatusCode = 404; context.Response.Close(); continue; }
                    byte[] bytes;
                    if (path == "/red.png")
                    {
                        using var bitmap = new Bitmap(16, 16);
                        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Red);
                        using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); bytes = stream.ToArray();
                        context.Response.ContentType = "image/png";
                    }
                    else if (path == "/download")
                    {
                        bytes = Encoding.UTF8.GetBytes("agbrowser download fixture");
                        context.Response.AddHeader("Content-Disposition", "attachment; filename=fixture.txt"); context.Response.ContentType = "application/octet-stream";
                    }
                    else
                    {
                        string title = path == "/two" ? "Second page" : path == "/three" ? "Third page" : path == "/popup" ? "Popup page" : path == "/icon-red" ? "Red icon" : path == "/icon-none" ? "No icon" : "First page";
                        string icon = path == "/icon-red" ? "<link rel=icon type=image/png href=/red.png>" : "";
                        var html = $"<!doctype html><title>{title}</title>{icon}<body><h1>WebView2 rendering verified</h1><div id=ad-fixture class=adsbygoogle>Ad placeholder</div><div id=content-fixture>Content</div><a id=popup target=_blank href=/popup>Open popup</a><a id=popup-opener target=_blank rel=opener href=/popup>Open login</a></body>";
                        bytes = Encoding.UTF8.GetBytes(html); context.Response.ContentType = "text/html";
                    }
                    context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
        string edgeDir = Path.Combine(root, "edge");
        Directory.CreateDirectory(Path.Combine(edgeDir, "Default"));
        File.WriteAllText(Path.Combine(edgeDir, "Local State"), "{\"profile\":{\"last_used\":\"Default\"}}");
        WriteEdgeFixture(edgeDir, port, false);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", edgeDir);
        InitializeTestApplication();
        try { VerifyAddressNormalization(); VerifyDebugPortCleanupError(); ChromeStoreTests.Run(root); } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        if (Environment.GetEnvironmentVariable("AGB_TEST_REAL_EDGE_READ") == "1")
        {
            try { VerifyRealEdgeRead(edgeDir); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        try { VerifySingleInstance(); } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        var clock = Stopwatch.StartNew();
        current = new BrowserWindow([$"http://127.0.0.1:{port}/one"]);
        current.Shown += async (_, _) => {
            try {
                var first = await WaitForTitle(current!, "First page");
                Console.WriteLine($"PASS first page rendered in {clock.ElapsedMilliseconds} ms (fresh test profile; local HTTP)");
                string browserArguments = await first.CoreWebView2.CallDevToolsProtocolMethodAsync("Browser.getBrowserCommandLine", "{}");
                Assert(browserArguments.Contains("--no-proxy-server") && browserArguments.Contains("MAP doubleclick.net 127.0.0.1") && browserArguments.Contains("MAP *.doubleclick.net 127.0.0.1"), "isolated browser uses loopback ad-host mapping");
                first.CoreWebView2.Navigate($"http://127.0.0.1:{port}/icon-red");
                await WaitForTitle(current!, "Red icon");
                await WaitUntil(() => FindButton(current!, "Red icon")?.Image is Bitmap icon && icon.GetPixel(10, 10).R > 200, "red favicon shown");
                first.CoreWebView2.Navigate($"http://localhost:{port}/icon-none");
                await WaitForTitle(current!, "No icon");
                await WaitUntil(() => FindButton(current!, "No icon") is { Image: null }, "old favicon cleared");
                first.CoreWebView2.Navigate($"http://127.0.0.1:{port}/one");
                await WaitForTitle(current!, "First page");
                Console.WriteLine("PASS real WebView2 favicon is cleared when a new page has no icon");
                var rendererFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited) rendererFailed.TrySetResult(true); };
                var crashRequest = first.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
                _ = crashRequest.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                Assert(await rendererFailed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "isolated renderer reported failure");
                var retry = FindControl<Button>(current!).Single(b => b.AccessibleName == "重新加载出错的页面");
                await WaitUntil(() => retry.Visible, "renderer failure recovery shown");
                var reloaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) reloaded.TrySetResult(true); };
                retry.PerformClick();
                Assert(await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(20)), "renderer reload completed");
                Assert(!retry.Visible && (await first.CoreWebView2.ExecuteScriptAsync("document.querySelector('h1').textContent")).Contains("WebView2 rendering verified"), "rendered page recovered");
                Console.WriteLine("PASS real isolated renderer failure, recovery UI and page reload");
                var editAddress = FindControl<TextBox>(current!).Single();
                Assert(editAddress.Focus(), "address can receive focus");
                editAddress.Text = "query still being typed";
                await first.CoreWebView2.ExecuteScriptAsync("document.title='Changed while typing'");
                await WaitUntil(() => current!.Text.StartsWith("Changed while typing"), "page title changed while editing address");
                Assert(editAddress.Text == "query still being typed", "page title change preserves address input");
                first.Focus();
                await WaitUntil(() => !editAddress.Focused, "address lost focus");
                await WaitUntil(() => editAddress.Text.EndsWith("/one", StringComparison.Ordinal), "address restored after editing loses focus");
                await first.CoreWebView2.ExecuteScriptAsync("document.title='First page'");
                await WaitForTitle(current!, "First page");
                Assert(editAddress.Focus(), "address focused for submit");
                editAddress.Text = $"http://127.0.0.1:{port}/two";
                typeof(TextBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editAddress, [new KeyEventArgs(Keys.Enter)]);
                await WaitForTitle(current!, "Second page");
                Assert(editAddress.Text.EndsWith("/two", StringComparison.Ordinal), "submitted address shows destination");
                first.CoreWebView2.GoBack(); await WaitForTitle(current!, "First page");
                Assert(editAddress.Focus(), "address focused for cancel");
                editAddress.Text = "cancel this input";
                var cancelSuggestions = (ToolStripDropDown)typeof(BrowserWindow).GetField("suggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current!)!;
                await WaitUntil(() => cancelSuggestions.Visible, "address suggestions shown before cancel");
                typeof(TextBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editAddress, [new KeyEventArgs(Keys.Escape)]);
                Assert(!cancelSuggestions.Visible, "first Escape dismisses suggestions");
                typeof(TextBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editAddress, [new KeyEventArgs(Keys.Escape)]);
                Assert(editAddress.Text.EndsWith("/one", StringComparison.Ordinal), "Escape restores page address");
                Console.WriteLine("PASS page title changes preserve address edits until focus leaves");
                var rail = (FlowLayoutPanel)typeof(BrowserWindow).GetField("rail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                int narrowRailWidth = (int)Math.Round(52 * current!.DeviceDpi / 96d);
                int expandedRailWidth = (int)Math.Round(240 * current.DeviceDpi / 96d);
                var railExpandedField = typeof(BrowserWindow).GetField("railExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var addTab = typeof(BrowserWindow).GetMethod("AddTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                for (int i = 0; i < 30; i++) await (Task)addTab.Invoke(current, [$"http://127.0.0.1:{port}/lazy-{i}", true, true, false])!;
                rail.PerformLayout();
                var railButtons = rail.Controls.OfType<Button>().ToArray();
                Assert(rail.VerticalScroll.Visible, "many tabs expose vertical scrolling");
                Assert(!rail.HorizontalScroll.Visible, $"tab rail must not scroll horizontally (viewport={rail.ClientSize.Width}, first-right={railButtons[0].Right})");
                Assert(railButtons[0].Right <= rail.ClientSize.Width, $"first tab is not horizontally clipped (viewport={rail.ClientSize.Width}, right={railButtons[0].Right})");
                rail.ScrollControlIntoView(railButtons[^1]);
                Assert(railButtons[^1].Bottom <= rail.ClientSize.Height && railButtons[^1].Top >= 0, $"last tab can be scrolled into view (top={railButtons[^1].Top}, bottom={railButtons[^1].Bottom}, viewport={rail.ClientSize.Height}, offset={rail.AutoScrollPosition.Y}, max={rail.VerticalScroll.Maximum}, large={rail.VerticalScroll.LargeChange})");
                rail.AutoScrollPosition = Point.Empty;
                railButtons[^1].PerformClick();
                await WaitUntil(() => railButtons[^1].Top >= 0 && railButtons[^1].Bottom <= rail.ClientSize.Height, "selected tab scrolls into view");
                await WaitUntil(() => Views(current!).Count(v => v.CoreWebView2 != null) == 2, "selected lazy tab initialized");
                var railTabs = (System.Collections.IList)typeof(BrowserWindow).GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                typeof(BrowserWindow).GetMethod("SelectTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(current, [railTabs[0]]);
                foreach (var button in railButtons.Skip(1)) button.ContextMenuStrip!.Items[0].PerformClick();
                await WaitUntil(() => !rail.VerticalScroll.Visible && rail.Width == ((bool)railExpandedField.GetValue(current)! ? expandedRailWidth : narrowRailWidth), "rail removes its scrollbar after tabs are closed");
                var closedHistory = (System.Collections.ICollection)typeof(BrowserWindow).GetField("recentlyClosed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                Assert(closedHistory.Count == 25, "closed-tab history is bounded");
                Console.WriteLine("PASS many vertical tabs remain visible and scroll without horizontal clipping");
                await WaitUntil(() => FavoriteButton(current!, "Fixture favorite") != null, "Edge favorites bar loaded");
                Assert(FavoriteFolder(current!, "其他收藏夹") != null, "empty Other favorites root shown in read-only mode");
                FavoriteButton(current!, "Fixture favorite")!.PerformClick();
                await WaitForTitle(current!, "Second page");
                first.CoreWebView2.GoBack(); await WaitForTitle(current!, "First page");
                WriteEdgeFixture(edgeDir, port, true);
                await WaitUntil(() => FavoriteButton(current!, "Added in Edge") != null, "Edge favorites refresh");
                Console.WriteLine("PASS default Edge favorites bar, navigation and refresh");
                string secondProfile = Path.Combine(edgeDir, "Profile 1"); Directory.CreateDirectory(secondProfile);
                string secondBookmarks = Path.Combine(secondProfile, "Bookmarks");
                string secondData = System.Text.Json.JsonSerializer.Serialize(new { roots = new { bookmark_bar = new { id = "1", type = "folder", children = new[] { new { id = "3", name = "Other profile favorite", type = "url", url = $"http://127.0.0.1:{port}/one" } } } } });
                File.WriteAllText(secondBookmarks, secondData);
                File.WriteAllText(Path.Combine(edgeDir, "Local State"), "{\"profile\":{\"last_used\":\"Profile 1\"}}");
                await WaitUntil(() => FavoriteButton(current!, "Other profile favorite") != null, "Edge profile switched");
                File.WriteAllText(secondBookmarks, "{invalid");
                await WaitUntil(() => FindControl<ToolStrip>(current!).SelectMany(x => x.Items.OfType<ToolStripLabel>()).Any(x => x.Text == "无法读取 Edge 当前配置") && FavoriteButton(current!, "Other profile favorite") == null, "unreadable favorites are not shown as current");
                File.WriteAllText(secondBookmarks, secondData);
                await WaitUntil(() => FavoriteButton(current!, "Other profile favorite") != null, "favorites recover when file becomes readable");
                string emptyProfile = Path.Combine(edgeDir, "Profile 2"); Directory.CreateDirectory(emptyProfile);
                File.WriteAllText(Path.Combine(edgeDir, "Local State"), "{\"profile\":{\"last_used\":\"Profile 2\"}}");
                await WaitUntil(() => FindControl<ToolStrip>(current!).SelectMany(x => x.Items.OfType<ToolStripLabel>()).Any(x => x.Text == "Edge 收藏栏为空"), "empty Edge profile replaces previous favorites");
                Assert(FavoriteButton(current!, "Other profile favorite") == null, "previous profile favorites not retained");
                File.WriteAllText(Path.Combine(emptyProfile, "Bookmarks"), System.Text.Json.JsonSerializer.Serialize(new { roots = new { bookmark_bar = new { id = "1", type = "folder", children = new[] { new { id = "5", name = "New profile favorite", type = "url", url = $"http://127.0.0.1:{port}/one" } } } } }));
                await WaitUntil(() => FavoriteButton(current!, "New profile favorite") != null, "new favorites file detected");
                File.Delete(Path.Combine(emptyProfile, "Bookmarks"));
                await WaitUntil(() => FavoriteButton(current!, "New profile favorite") == null && FindControl<ToolStrip>(current!).SelectMany(x => x.Items.OfType<ToolStripLabel>()).Any(x => x.Text == "Edge 收藏栏为空"), "removed favorites file clears old favorites");
                File.WriteAllText(Path.Combine(edgeDir, "Local State"), "{\"profile\":{\"last_used\":\"Profile 3\"}}");
                await WaitUntil(() => FindControl<ToolStrip>(current!).SelectMany(x => x.Items.OfType<ToolStripLabel>()).Any(x => x.Text == "无法读取 Edge 当前配置"), "missing profile is reported as unavailable");
                File.WriteAllText(Path.Combine(edgeDir, "Local State"), "{\"profile\":{\"last_used\":\"Default\"}}");
                await WaitUntil(() => FavoriteButton(current!, "Added in Edge") != null, "Edge profile switched back");
                Console.WriteLine("PASS Edge active profile changes reflected without modifying Edge files");
                await VerifyNativeBridge(current!, port);
                await WaitUntil(() => FavoriteButton(current!, "Added in Edge") != null, "file fallback after Edge disconnect");
                await VerifyWorkerBridge(current!, port);
                await WaitUntil(() => FavoriteButton(current!, "Added in Edge") != null, "file fallback after worker disconnect");
                var rendered = await first.CoreWebView2.ExecuteScriptAsync("document.querySelector('h1').textContent");
                Assert(rendered.Contains("WebView2 rendering verified"), "rendered DOM");
                var downloadDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                string downloadPath = Path.Combine(root, "fixture-download.txt");
                first.CoreWebView2.DownloadStarting += (_, e) => {
                    e.ResultFilePath = downloadPath; e.Handled = true;
                    e.DownloadOperation.StateChanged += (_, _) => { if (e.DownloadOperation.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Completed) downloadDone.TrySetResult(true); else if (e.DownloadOperation.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.Interrupted) downloadDone.TrySetResult(false); };
                };
                await first.CoreWebView2.ExecuteScriptAsync($"location.href='http://127.0.0.1:{port}/download'");
                Assert(await downloadDone.Task.WaitAsync(TimeSpan.FromSeconds(15)), "WebView2 download completed");
                Assert(File.ReadAllText(downloadPath) == "agbrowser download fixture", "downloaded file content");
                var downloadButton = FindControl<Button>(current!).Single(b => b.AccessibleName == "下载");
                downloadButton.PerformClick();
                await WaitUntil(() => first.CoreWebView2.IsDefaultDownloadDialogOpen, "download dialog opened");
                first.CoreWebView2.CloseDefaultDownloadDialog();
                Console.WriteLine("PASS WebView2 download and download-dialog entry");
                var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri.Contains("googleads.g.doubleclick.net")) blocked.TrySetResult(e.Response?.StatusCode == 403); };
                await first.CoreWebView2.ExecuteScriptAsync("(()=>{const img=new Image();img.src='https://googleads.g.doubleclick.net/agb-test.png';})() ");
                Assert(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)), "ad request blocked by WebView2");
                var iframeBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri.Contains("doubleclick.net/agb-frame")) iframeBlocked.TrySetResult(e.Response?.StatusCode == 403); };
                await first.CoreWebView2.ExecuteScriptAsync("(()=>{const frame=document.createElement('iframe');frame.src='https://doubleclick.net/agb-frame';document.body.appendChild(frame);})()");
                Assert(await iframeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10)), "ad iframe blocked by WebView2");
                var customPortBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri.Contains("/agb-custom-port")) customPortBlocked.TrySetResult(e.Response?.StatusCode == 403); };
                await first.CoreWebView2.ExecuteScriptAsync($"(()=>{{const img=new Image();img.src='http://doubleclick.net:{port}/agb-custom-port';}})()");
                Assert(await customPortBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5)), "ad host blocked on custom port");
                var subdomainPortBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => { if (e.Request.Uri.Contains("/agb-subdomain-port")) subdomainPortBlocked.TrySetResult(e.Response?.StatusCode == 403); };
                await first.CoreWebView2.ExecuteScriptAsync($"(()=>{{const img=new Image();img.src='http://ads.doubleclick.net:{port}/agb-subdomain-port';}})()");
                Assert(await subdomainPortBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5)), "ad subdomain blocked on custom port");
                var similarHostAllowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => {
                    if (!e.Request.Uri.Contains("/agb-similar-host")) return;
                    similarHostAllowed.TrySetResult(e.Response is null);
                    e.Response = first.CoreWebView2.Environment.CreateWebResourceResponse(null, 204, "Test intercepted", "");
                };
                await first.CoreWebView2.ExecuteScriptAsync($"(()=>{{const img=new Image();img.src='http://doubleclick.net.example.test:{port}/agb-similar-host';}})()");
                Assert(await similarHostAllowed.Task.WaitAsync(TimeSpan.FromSeconds(5)), "similar host is not blocked");
                Console.WriteLine("PASS actual WebView2 ad resource and iframe requests returned 403");
                Console.WriteLine("PASS ad domains and subdomains on custom ports are blocked without blocking similar hosts");
                var settings = typeof(BrowserWindow).GetField("adBlock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                var setEnabled = settings.GetType().GetMethod("SetEnabled")!;
                setEnabled.Invoke(settings, ["127.0.0.1", false]);
                var allowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => {
                    if (!e.Request.Uri.Contains("doubleclick.net/agb-allowed")) return;
                    allowed.TrySetResult(e.Response == null);
                    e.Response = first.CoreWebView2.Environment.CreateWebResourceResponse(null, 204, "Test intercepted", "");
                };
                await first.CoreWebView2.ExecuteScriptAsync("(()=>{const img=new Image();img.src='https://doubleclick.net/agb-allowed';})()");
                Assert(await allowed.Task.WaitAsync(TimeSpan.FromSeconds(10)), "site ad-block exception");
                var portAllowed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.WebResourceRequested += (_, e) => {
                    if (!e.Request.Uri.Contains("/agb-port-allowed")) return;
                    portAllowed.TrySetResult(e.Response is null);
                    e.Response = first.CoreWebView2.Environment.CreateWebResourceResponse(null, 204, "Test intercepted", "");
                };
                await first.CoreWebView2.ExecuteScriptAsync($"(()=>{{const img=new Image();img.src='http://doubleclick.net:{port}/agb-port-allowed';}})()");
                Assert(await portAllowed.Task.WaitAsync(TimeSpan.FromSeconds(5)), "site exception allows custom-port request");
                var reloadWhenDisabled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) reloadWhenDisabled.TrySetResult(true); };
                first.CoreWebView2.Reload();
                Assert(await reloadWhenDisabled.Task.WaitAsync(TimeSpan.FromSeconds(15)), "reload with ad blocking disabled");
                await WaitForScriptResult(first, "getComputedStyle(document.getElementById('ad-fixture')).display", "\"block\"");
                setEnabled.Invoke(settings, ["127.0.0.1", true]);
                var reloadWhenEnabled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                first.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) reloadWhenEnabled.TrySetResult(true); };
                first.CoreWebView2.Reload();
                Assert(await reloadWhenEnabled.Task.WaitAsync(TimeSpan.FromSeconds(15)), "reload with ad blocking enabled");
                await WaitForScriptResult(first, "getComputedStyle(document.getElementById('ad-fixture')).display", "\"none\"");
                await WaitForScriptResult(first, "getComputedStyle(document.getElementById('content-fixture')).display", "\"block\"");
                Console.WriteLine("PASS per-site ad-block exception without making a network request");
                Console.WriteLine("PASS targeted ad placeholder hiding respects per-site exception and keeps ordinary content");
                var shortcut = typeof(BrowserWindow).GetMethod("OnShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var newTabKey = new KeyEventArgs(Keys.Control | Keys.T);
                shortcut.Invoke(current, [first, newTabKey]);
                shortcut.Invoke(current, [first, newTabKey]); // KeyPreview and control may see the same event.
                Assert(newTabKey.Handled && Views(current!).Count == 1, "shortcut is consumed and deferred");
                await WaitUntil(() => Views(current!).Count == 2 && current!.Text.StartsWith("新建标签页"), "deferred new-tab shortcut");
                Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "new blank tab does not initialize another WebView2");
                var copyKey = new KeyEventArgs(Keys.Control | Keys.C);
                shortcut.Invoke(current, [editAddress, copyKey]);
                Assert(!copyKey.Handled, "ordinary page shortcut remains available");
                var closeKey = new KeyEventArgs(Keys.Control | Keys.W);
                shortcut.Invoke(current, [editAddress, closeKey]);
                await WaitUntil(() => Views(current!).Count == 1, "deferred close-tab shortcut");
                Console.WriteLine("PASS forwarded shortcut routing is deferred, deduplicated and leaves Ctrl+C alone");
                var plus = FindButton(current!, "+"); Assert(plus != null, "new tab button"); plus!.PerformClick();
                await WaitUntil(() => Views(current!).Count == 2 && FindControl<TextBox>(current!).Single().Enabled && current!.Text.StartsWith("新建标签页"), "new tab shown and selected");
                Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "blank tab remains lazy before navigation");
                var address = FindControl<TextBox>(current!).Single();
                await WaitUntil(() => address.Focused && address.Text.Length == 0, "new tab focuses an empty address field");
                address.Text = $"http://127.0.0.1:{port}/two";
                typeof(BrowserWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, [address.Text]);
                await WaitForTitle(current!, "Second page");
                await WaitUntil(() => FindButton(current!, "First page") != null && FindButton(current!, "Second page") != null, "both tab buttons");
                var browserType = typeof(BrowserWindow);
                var expandRail = browserType.GetMethod("SetRailExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var railPanel = (Panel)browserType.GetField("sidePanel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                var page = Views(current!).Single(v => v.Visible);
                await Task.Delay(150); // settle navigation and any initial WebView layout
                string pageWidth = await page.CoreWebView2.ExecuteScriptAsync("window.innerWidth");
                int viewWidth = page.Width;
                await page.CoreWebView2.ExecuteScriptAsync("window.__agbResizeCount = 0; window.addEventListener('resize', () => window.__agbResizeCount++);");
                expandRail.Invoke(current, [true]);
                var firstButton = FindButton(current!, "First page")!;
                var secondButton = FindButton(current!, "Second page")!;
                Assert(firstButton.Width == (int)Math.Round(228 * current!.DeviceDpi / 96d) && rail.Width >= expandedRailWidth, "hover expansion widens rail and tab");
                Assert(railPanel.Parent == current && current!.Controls.GetChildIndex(railPanel) == 0, "expanded rail is above the page layout");
                await Task.Delay(150);
                Assert(page.Width == viewWidth && await page.CoreWebView2.ExecuteScriptAsync("window.innerWidth") == pageWidth && await page.CoreWebView2.ExecuteScriptAsync("window.__agbResizeCount") == "0", "expanding the rail does not resize the WebView or page");
                Assert((string)firstButton.GetType().GetProperty("DisplayTitle")!.GetValue(firstButton)! == "First page", "expanded tab has page title");
                Size originalSize = current.ClientSize;
                current.ClientSize = new Size(originalSize.Width - 80, originalSize.Height - 60);
                await WaitUntil(() => page.Width == viewWidth - 80 && railPanel.Height == current.ClientSize.Height, "resized window updates page and rail height");
                current.ClientSize = originalSize;
                await WaitUntil(() => page.Width == viewWidth && railPanel.Height == current.ClientSize.Height, "restored window updates page and rail height");
                var tabsForDrag = (System.Collections.IList)browserType.GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                object sourceTab = tabsForDrag[0]!;
                var dragging = browserType.GetField("draggingTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var format = (string)browserType.GetField("tabDragFormat", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                var dragData = new DataObject(); dragData.SetData(format, "tab");
                var dropPoint = secondButton.PointToScreen(new Point(8, secondButton.Height - 2));
                dragging.SetValue(current, sourceTab);
                try {
                    var drop = new DragEventArgs(dragData, 0, dropPoint.X, dropPoint.Y, DragDropEffects.Move, DragDropEffects.Move);
                    typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(secondButton, [drop]);
                    Assert(ReferenceEquals(tabsForDrag[1], sourceTab) && rail.Controls.GetChildIndex(firstButton) == 1, "tab drop reorders model and rail");
                    Assert(current!.Text.StartsWith("Second page"), "tab reorder preserves active page");
                    await WaitUntil(() => SessionMatches(Path.Combine(root, "session.json"), 2, 0, "/two"), "reordered session saved");
                } finally { dragging.SetValue(current, null); }
                browserType.GetMethod("MoveTab", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(current, [sourceTab, 0]);
                expandRail.Invoke(current, [false]);
                Assert(firstButton.Width == (int)Math.Round(40 * current!.DeviceDpi / 96d) && rail.Width == narrowRailWidth && page.Width == viewWidth,
                    $"rail collapses to compact width without resizing the page (button={firstButton.Width}, rail={rail.Width}/{narrowRailWidth}, page={page.Width}/{viewWidth}, expanded={browserType.GetField("railExpanded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current)})");
                Console.WriteLine("PASS overlay rail preserves page width, follows window resize, and supports simulated drag-drop order");
                var firstTabKey = new KeyEventArgs(Keys.Control | Keys.D1);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), firstTabKey]);
                Assert(firstTabKey.Handled && current!.Text.StartsWith("Second page"), "numbered tab shortcut consumed and deferred");
                await WaitUntil(() => current!.Text.StartsWith("First page"), "Ctrl+1 selects first tab");
                var absentTabKey = new KeyEventArgs(Keys.Control | Keys.D8);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), absentTabKey]);
                Assert(absentTabKey.Handled, "unavailable numbered tab shortcut is consumed");
                await Task.Delay(100);
                Assert(current!.Text.StartsWith("First page"), "unavailable tab index leaves selection unchanged");
                var lastTabKey = new KeyEventArgs(Keys.Control | Keys.D9);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), lastTabKey]);
                await WaitUntil(() => current!.Text.StartsWith("Second page"), "Ctrl+9 selects last tab");
                var modifiedNumber = new KeyEventArgs(Keys.Control | Keys.Shift | Keys.D1);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), modifiedNumber]);
                Assert(!modifiedNumber.Handled, "other modified number shortcuts remain available to the page");
                Console.WriteLine("PASS numbered tab shortcuts select first and last without intercepting other modifiers");
                Assert(editAddress.Focus(), "address focused before tab switch");
                editAddress.Text = "unfinished entry";
                FindButton(current!, "First page")!.PerformClick();
                Assert(current!.Text.Contains("First page"), "switch to first tab");
                Assert(editAddress.Text.EndsWith("/one", StringComparison.Ordinal), "switching tabs shows selected tab address");
                FindButton(current!, "Second page")!.PerformClick();
                await WaitUntil(() => SessionMatches(Path.Combine(root, "session.json"), 2, 1, "/two"), "live session saved before close");
                Console.WriteLine("PASS live session checkpoint before normal shutdown");
                Console.WriteLine("PASS new tab, navigation, switching");
                var popupSource = Views(current!).First(v => v.Visible);
                await popupSource.CoreWebView2.ExecuteScriptAsync("document.getElementById('popup').click()");
                await WaitForTitle(current!, "Popup page");
                await WaitUntil(() => FindButton(current!, "Popup page") != null, "popup tab button");
                var popupButton = FindButton(current!, "Popup page")!;
                FindButton(current!, "Second page")!.ContextMenuStrip!.Items[0].PerformClick();
                await WaitUntil(() => Views(current!).Count == 2, "background tab closed");
                FindButton(current!, "First page")!.ContextMenuStrip!.Items[0].PerformClick();
                await WaitUntil(() => Views(current!).Count == 1, "another background tab closed");
                var reopenKey = new KeyEventArgs(Keys.Control | Keys.Shift | Keys.T);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), reopenKey]);
                Assert(reopenKey.Handled, "reopen shortcut consumed");
                await WaitUntil(() => Views(current!).Count == 2 && current!.Text.StartsWith("First page"), "most recently closed tab reopened first");
                await WaitUntil(() => rail.Controls.GetChildIndex(FindButton(current!, "First page")!) == 0, "first reopened tab restored to previous position");
                reopenKey = new KeyEventArgs(Keys.Control | Keys.Shift | Keys.T);
                shortcut.Invoke(current, [Views(current!).First(v => v.Visible), reopenKey]);
                Assert(reopenKey.Handled, "second reopen shortcut consumed");
                await WaitUntil(() => Views(current!).Count == 3 && current!.Text.StartsWith("Second page"), "closed tab reopened");
                await WaitUntil(() => rail.Controls.GetChildIndex(FindButton(current!, "Second page")!) == 1, "reopened tab restored to previous position");
                popupButton.ContextMenuStrip!.Items[0].PerformClick();
                await WaitUntil(() => Views(current!).Count == 2, "popup closed");
                Console.WriteLine("PASS popup routing and reopening closed tabs in reverse order at their previous positions");
                FindButton(current!, "First page")!.PerformClick();
                var parentView = Views(current!).Single(v => v.Visible);
                await parentView.CoreWebView2.ExecuteScriptAsync("window.agbLoginMessage='';window.addEventListener('message', e => { if (e.data === 'login-complete') window.agbLoginMessage=e.data; });document.getElementById('popup-opener').click()");
                var loginPopup = await WaitForTitle(current!, "Popup page");
                Assert(await loginPopup.CoreWebView2.ExecuteScriptAsync("!!window.opener") == "true", "popup retains opener");
                await loginPopup.CoreWebView2.ExecuteScriptAsync("window.opener.postMessage('login-complete','*')");
                await WaitForScriptResult(parentView, "window.agbLoginMessage", "\"login-complete\"");
                var closePopup = loginPopup.CoreWebView2.ExecuteScriptAsync("window.close()");
                _ = closePopup.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                await WaitUntil(() => Views(current!).Count == 2 && current!.Text.StartsWith("First page"), "script-opened popup closed itself and returned to opener");
                await parentView.CoreWebView2.ExecuteScriptAsync("window.close()");
                Assert(Views(current!).Count == 2, "normal tab cannot close itself");
                FindButton(current!, "Second page")!.PerformClick();
                Console.WriteLine("PASS opener communication and script-close for login popup, with normal tab retained");
                current!.Close();
            } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; current?.Close(); }
        };
        Application.Run(current);
        if (status == 0)
        {
            string file = Path.Combine(root, "session.json");
            if (!File.Exists(file) || System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("urls").GetArrayLength() != 2) { Console.Error.WriteLine("FAIL session saved"); status = 1; }
            else {
                Console.WriteLine("PASS session saved");
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        await WaitUntil(() => FindButton(current!, "Second page") != null && FindButton(current!, "First page") != null, "session restored with tab titles");
                        Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "only active tab initialized on restore");
                        Assert(current!.Text.Contains("Second page"), "selected tab restored");
                        FindButton(current!, "First page")!.PerformClick();
                        await WaitForTitle(current!, "First page");
                        Console.WriteLine("PASS selected tab, lazy restore and on-demand tab navigation");
                        string? liveUrl = Environment.GetEnvironmentVariable("AGB_LIVE_URL");
                        if (!string.IsNullOrEmpty(liveUrl))
                        {
                            typeof(BrowserWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, [liveUrl]);
                            var live = Views(current!).First(v => v.Visible && v.CoreWebView2 != null);
                            await WaitUntil(() => live.CoreWebView2.Source.StartsWith(liveUrl, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(live.CoreWebView2.DocumentTitle), "live website");
                            string result = "";
                            for (int i = 0; i < 200; i++)
                            {
                                try { result = await live.CoreWebView2.ExecuteScriptAsync("document.readyState"); } catch { }
                                if (result.Contains("complete") || result.Contains("interactive")) break;
                                await Task.Delay(100);
                            }
                            Assert(result.Contains("complete") || result.Contains("interactive"), "live DOM ready");
                            string expectedTitle = Environment.GetEnvironmentVariable("AGB_LIVE_EXPECT_TITLE") ?? "";
                            if (!string.IsNullOrEmpty(expectedTitle))
                                Assert(live.CoreWebView2.DocumentTitle.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase), "live website title is not the expected page: " + live.CoreWebView2.DocumentTitle);
                            Assert(!live.CoreWebView2.DocumentTitle.Contains("Forbidden", StringComparison.OrdinalIgnoreCase), "live website returned Forbidden");
                            Console.WriteLine("PASS live website opened: " + live.CoreWebView2.DocumentTitle);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
                if (status == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGB_LIVE_URL")))
                {
                    current = new BrowserWindow([$"http://127.0.0.1:{port}/three"]);
                    current.Shown += async (_, _) => {
                        try {
                            await WaitForTitle(current!, "Third page");
                            await WaitUntil(() => Views(current!).Count == 3, "external URL alongside restored tabs");
                            Console.WriteLine("PASS opening an external URL retains the saved session");
                        } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                        finally { current?.Close(); }
                    };
                    Application.Run(current);
                    if (status == 0)
                    {
                        string sessionFile = Path.Combine(root, "session.json");
                        File.WriteAllText(sessionFile, "{broken session");
                        current = new BrowserWindow([]);
                        current.Shown += async (_, _) => {
                            try { await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "fallback session initialized"); }
                            catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                            finally { current?.Close(); }
                        };
                        Application.Run(current);
                        if (File.ReadAllText(sessionFile) != "{broken session") { Console.Error.WriteLine("FAIL unreadable session overwritten"); status = 1; }
                        else Console.WriteLine("PASS unreadable session preserved for recovery");
                        if (status == 0)
                        {
                            const string futureSession = "{\"version\":2,\"urls\":[\"https://example.com/\"],\"activeIndex\":0}";
                            File.WriteAllText(sessionFile, futureSession);
                            current = new BrowserWindow([]);
                            current.Shown += async (_, _) => {
                                try { await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "future-version fallback initialized"); }
                                catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                                finally { current?.Close(); }
                            };
                            Application.Run(current);
                            if (File.ReadAllText(sessionFile) != futureSession) { Console.Error.WriteLine("FAIL future session version overwritten"); status = 1; }
                            else Console.WriteLine("PASS unknown session version preserved for recovery");
                        }
                        if (status == 0)
                        {
                            string inaccessibleRoot = Path.Combine(root, "session-path-directory-fixture");
                            string directoryAtSessionPath = Path.Combine(inaccessibleRoot, "session.json");
                            Directory.CreateDirectory(directoryAtSessionPath);
                            string sentinel = Path.Combine(directoryAtSessionPath, "sentinel");
                            File.WriteAllText(sentinel, "keep");
                            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", inaccessibleRoot);
                            current = new BrowserWindow([]);
                            current.Shown += async (_, _) => {
                                try {
                                    await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "inaccessible session fallback initialized");
                                    Assert(!(bool)typeof(BrowserWindow).GetField("sessionReadable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "existing unreadable session path must not be treated as missing");
                                    Assert(FindControl<Label>(current!).Any(x => x.Visible && x.Text.Contains("本次标签更改不会保存")), "unreadable session warning is visible");
                                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                                finally { current?.Close(); }
                            };
                            Application.Run(current);
                            if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "keep" || File.Exists(Path.Combine(inaccessibleRoot, "session.json.tmp")))
                            { Console.Error.WriteLine("FAIL inaccessible session path was modified"); status = 1; }
                            else if (status == 0) Console.WriteLine("PASS inaccessible session path is preserved rather than treated as absent");
                        }
                        if (status == 0)
                        {
                            string writeFailureRoot = Path.Combine(root, "session-write-failure-fixture");
                            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", writeFailureRoot);
                            current = new BrowserWindow([$"http://127.0.0.1:{port}/one"]);
                            current.Shown += async (_, _) => {
                                try {
                                    await WaitForTitle(current!, "First page");
                                    await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "write failure fixture initialized");
                                    string path = Path.Combine(writeFailureRoot, "session.json");
                                    if (File.Exists(path)) File.Move(path, path + ".previous");
                                    Directory.CreateDirectory(path);
                                    var save = typeof(BrowserWindow).GetMethod("SaveSession", BindingFlags.NonPublic | BindingFlags.Instance)!;
                                    save.Invoke(current, null);
                                    Assert(FindControl<Label>(current!).Any(x => x.Visible && x.Text.Contains("无法保存当前标签")), "write failure warning is visible");
                                    Directory.Delete(path);
                                    save.Invoke(current, null);
                                    Assert(File.Exists(path), "session saved after write obstruction removed");
                                    Assert(!FindControl<Label>(current!).Any(x => x.Visible && x.Text.Contains("无法保存当前标签")), "write failure warning clears after successful save");
                                    Console.WriteLine("PASS session write failures are visible and clear after successful retry");
                                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                                finally { current?.Close(); }
                            };
                            Application.Run(current);
                        }
                        if (status == 0)
                        {
                            string failureRoot = Path.Combine(root, "browser-failure-fixture");
                            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", failureRoot);
                            current = new BrowserWindow([$"http://127.0.0.1:{port}/one"]);
                            current.Shown += async (_, _) => {
                                try {
                                    await WaitForTitle(current!, "First page");
                                    await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "failure fixture initialized");
                                    FindButton(current!, "+")!.PerformClick();
                                    await WaitUntil(() => Views(current!).Count == 2 && current!.Text.StartsWith("新建标签页"), "second failure-fixture tab ready");
                                    typeof(BrowserWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, [$"http://127.0.0.1:{port}/two"]);
                                    await WaitForTitle(current!, "Second page");
                                    var fixtureView = Views(current!).Single(v => v.Visible);
                                    Assert(Path.GetFullPath(fixtureView.CoreWebView2.Environment.UserDataFolder).Equals(Path.GetFullPath(Path.Combine(failureRoot, "profile")), StringComparison.OrdinalIgnoreCase), "browser failure uses isolated profile");
                                    Assert(Views(current!).All(v => v.CoreWebView2.BrowserProcessId == fixtureView.CoreWebView2.BrowserProcessId), "fixture tabs share only their isolated browser process");
                                    var browserFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                    fixtureView.CoreWebView2.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited) browserFailed.TrySetResult(true); };
                                    var browserCrash = fixtureView.CoreWebView2.CallDevToolsProtocolMethodAsync("Browser.crash", "{}");
                                    _ = browserCrash.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                    Assert(await browserFailed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "isolated browser process reported failure");
                                    Assert(SessionMatches(Path.Combine(failureRoot, "session.json"), 2, 1, "/two"), "browser failure preserves both tabs and active tab");
                                    Assert(!FindControl<TextBox>(current!).Single().Enabled, "browser failure disables address field");
                                    Assert(FindControl<Label>(current!).Any(x => x.Visible && x.Text.Contains("浏览器内核已退出")), "browser failure explains restart");
                                    typeof(BrowserWindow).GetMethod("OpenExternal", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(current, [new[] { $"http://127.0.0.1:{port}/three" }]);
                                    await WaitUntil(() => SessionMatches(Path.Combine(failureRoot, "session.json"), 3, 2, "/three"), "external link queued during browser failure");
                                    var recover = FindControl<Button>(current!).Single(b => b.AccessibleName == "恢复浏览器");
                                    await WaitUntil(() => recover.Visible && recover.Enabled, "browser resources released for recovery");
                                    recover.PerformClick();
                                    await WaitUntil(() => Views(current!).Count == 3 && Views(current!).Any(v => v.Visible && v.CoreWebView2?.DocumentTitle == "Third page") && !recover.Visible, "queued external link opened after in-place recovery");
                                    Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 2, "inactive tab remains lazy after in-place recovery");
                                    FindButton(current!, "Third page")!.ContextMenuStrip!.Items[0].PerformClick();
                                    FindButton(current!, "First page")!.PerformClick();
                                    await WaitForTitle(current!, "First page");
                                    FindButton(current!, "Second page")!.PerformClick();
                                    Assert(current!.Text.Contains("Second page"), "selection works after in-place recovery");
                                    Console.WriteLine("PASS real browser-process failure, queued external link and in-place tab recovery");
                                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                                finally { current?.Close(); }
                            };
                            Application.Run(current);
                            if (status == 0)
                            {
                                current = new BrowserWindow([]);
                                current.Shown += async (_, _) => {
                                    try {
                                        await WaitForTitle(current!, "Second page");
                                        await WaitUntil(() => Views(current!).Count == 2 && FindButton(current!, "First page") != null, "tabs restored after real browser failure");
                                        Assert(current!.Text.Contains("Second page") && Views(current!).Count(v => v.CoreWebView2 != null) == 1, "active tab restored lazily after browser failure");
                                        FindButton(current!, "First page")!.PerformClick();
                                        await WaitForTitle(current!, "First page");
                                        Console.WriteLine("PASS reopen after real browser-process failure restores both tabs and selection");
                                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                                    finally { current?.Close(); }
                                };
                                Application.Run(current);
                            }
                        }
                    }
                }
            }
        }
        if (status == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGB_LIVE_URL")))
        {
            string appearanceRoot = Path.Combine(root, "appearance-fixture");
            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", appearanceRoot);
            current = new BrowserWindow([$"http://127.0.0.1:{port}/icon-red"]);
            current.Shown += async (_, _) => {
                try {
                    await WaitForTitle(current!, "Red icon");
                    await WaitUntil(() => FindButton(current!, "Red icon")?.Image is Bitmap image && image.GetPixel(10, 10).R > 200, "favicon loaded before session save");
                    FindButton(current!, "+")!.PerformClick();
                    await WaitUntil(() => Views(current!).Count == 2 && current!.Text.StartsWith("新建标签页"), "appearance second tab ready");
                    typeof(BrowserWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, [$"http://127.0.0.1:{port}/two"]);
                    await WaitForTitle(current!, "Second page");
                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                finally { current?.Close(); }
            };
            Application.Run(current);
            if (status == 0)
            {
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        await WaitForTitle(current!, "Second page");
                        await WaitUntil(() => FindButton(current!, "Red icon")?.Image is Bitmap image && image.GetPixel(10, 10).R > 200, "cached favicon and title restored");
                        Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "cached favicon restored without initializing inactive tab");
                        Console.WriteLine("PASS inactive tab title and favicon restored without loading its WebView2");
                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
            }
            if (status == 0)
            {
                string sessionFile = Path.Combine(appearanceRoot, "session.json");
                File.WriteAllText(sessionFile, System.Text.Json.JsonSerializer.Serialize(new { version = 1, urls = new[] { $"http://127.0.0.1:{port}/icon-red", $"http://127.0.0.1:{port}/two" }, activeIndex = 1, titles = new[] { "Red icon", "Second page" }, icons = new string?[] { "not valid base64", null } }));
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        await WaitForTitle(current!, "Second page");
                        await WaitUntil(() => FindButton(current!, "Red icon") is { Image: null }, "invalid cached icon ignored");
                        Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "invalid icon does not initialize background tab");
                        Console.WriteLine("PASS invalid cached favicon is ignored without losing tabs");
                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
            }
            if (status == 0)
            {
                File.WriteAllText(Path.Combine(appearanceRoot, "session.json"), System.Text.Json.JsonSerializer.Serialize(new[] { $"http://127.0.0.1:{port}/icon-red", $"http://127.0.0.1:{port}/two" }));
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        await WaitForTitle(current!, "Red icon");
                        await WaitUntil(() => FindButton(current!, $"http://127.0.0.1:{port}/two") != null, "legacy array session restored");
                        await WaitUntil(() => FindButton(current!, "Red icon")?.Image != null, "legacy session favicon loaded");
                        Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "legacy session remains lazy");
                        Console.WriteLine("PASS legacy array session restores without appearance metadata");
                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
            }
            if (status == 0)
            {
                string sessionFile = Path.Combine(appearanceRoot, "session.json");
                using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sessionFile));
                string icon = saved.RootElement.GetProperty("icons")[0].GetString()!;
                string url = $"http://127.0.0.1:{port}/icon-red";
                File.WriteAllText(sessionFile, System.Text.Json.JsonSerializer.Serialize(new { version = 1, urls = Enumerable.Repeat(url, 100).ToArray(), activeIndex = 0, titles = Enumerable.Repeat("Red icon", 100).ToArray(), icons = Enumerable.Repeat(icon, 100).ToArray() }));
                var restoreClock = Stopwatch.StartNew();
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)! && Views(current!).Count == 100, "100-tab session restored");
                        await WaitForTitle(current!, "Red icon");
                        var rail = (FlowLayoutPanel)typeof(BrowserWindow).GetField("rail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                        Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "100-tab session initializes only active WebView2");
                        Assert(rail.Controls.OfType<Button>().Count(b => b.Image != null) >= 99, "cached icons restored for inactive tabs");
                        Console.WriteLine($"PASS 100-tab session restored in {restoreClock.ElapsedMilliseconds} ms (existing test process; isolated local profile, not cold process startup)");
                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
            }
        }
        if (status == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGB_LIVE_URL")))
        {
            string startupRoot = Path.Combine(root, "startup-external-queue-fixture");
            Directory.CreateDirectory(startupRoot);
            string[] savedUrls = Enumerable.Range(0, 100).Select(i => $"http://127.0.0.1:{port}/one?tab={i}").ToArray();
            File.WriteAllText(Path.Combine(startupRoot, "session.json"), System.Text.Json.JsonSerializer.Serialize(new { version = 1, urls = savedUrls, activeIndex = 99 }));
            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", startupRoot);
            current = new BrowserWindow([]);
            var startupType = typeof(BrowserWindow);
            var startupRestored = startupType.GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var startupEnvironment = startupType.GetField("environment", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var startupPending = (System.Collections.IList)startupType.GetField("pendingExternalUrls", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current)!;
            var startupTabs = (System.Collections.IEnumerable)startupType.GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current)!;
            var openExternal = startupType.GetMethod("OpenExternal", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var startupRail = (FlowLayoutPanel)startupType.GetField("rail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current)!;
            string firstUrl = $"http://127.0.0.1:{port}/two";
            string secondUrl = $"http://127.0.0.1:{port}/three";
            bool queuedFirst = false, queuedSecond = false;
            string? queueError = null;
            startupRail.ControlAdded += (_, _) => {
                if ((bool)startupRestored.GetValue(current!)! || startupEnvironment.GetValue(current!) is null) return;
                if (!queuedFirst)
                {
                    queuedFirst = true;
                    openExternal.Invoke(current, [new[] { firstUrl }]);
                    if (startupPending.Count != 1) queueError = "first external link was not queued";
                }
                else if (!queuedSecond && startupPending.Count == 0 && startupTabs.Cast<object>().Any(tab => (string)tab.GetType().GetProperty("Url")!.GetValue(tab)! == firstUrl))
                {
                    queuedSecond = true;
                    openExternal.Invoke(current, [new[] { secondUrl }]);
                    if (startupPending.Count != 1) queueError = "link arriving during queue drain was not queued";
                }
            };
            current.Shown += async (_, _) => {
                try {
                    await WaitUntil(() => (bool)startupRestored.GetValue(current!)! && Views(current!).Count == 102 && startupPending.Count == 0, "startup queue fully drained");
                    Assert(queuedFirst && queuedSecond && queueError is null, queueError ?? "both startup queue timings were exercised");
                    await WaitForTitle(current!, "Third page");
                    var urls = startupTabs.Cast<object>().Select(tab => (string)tab.GetType().GetProperty("Url")!.GetValue(tab)!).ToArray();
                    Assert(urls.Take(100).SequenceEqual(savedUrls) && urls[100] == firstUrl && urls[101] == secondUrl, "saved tabs and queued links retain order");
                    Console.WriteLine("PASS links arriving during startup and queue drain are preserved in order");
                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                finally { current?.Close(); }
            };
            Application.Run(current);
        }
        if (status == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGB_LIVE_URL")))
        {
            string blankRoot = Path.Combine(root, "native-blank-tabs");
            Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", blankRoot);
            current = new BrowserWindow([]);
            current.Shown += async (_, _) => {
                try {
                    var type = typeof(BrowserWindow);
                    await WaitUntil(() => (bool)type.GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)! && current!.Text.StartsWith("新建标签页"), "native blank tab ready");
                    var page = (Control)type.GetField("newTabPage", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                    Assert(page.Visible && Views(current!).Count == 1 && Views(current!).All(view => view.CoreWebView2 is null), "fresh blank tab does not create WebView2");
                    var searchButton = FindControl<Button>(current!).Single(button => button.AccessibleName == "定位地址栏以搜索或输入网址");
                    Size normalSize = current!.ClientSize;
                    current.ClientSize = new Size(600, 400);
                    Rectangle searchBounds = page.RectangleToClient(searchButton.RectangleToScreen(searchButton.ClientRectangle));
                    Assert(page.ClientRectangle.Contains(searchBounds), "new-tab search remains inside the resized window");
                    current.ClientSize = normalSize;
                    searchButton.PerformClick();
                    Assert(FindControl<TextBox>(current!).Single().Focused, "new-tab search action focuses the address bar");
                    var addTab = type.GetMethod("AddTab", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    for (int i = 0; i < 10; i++) await (Task)addTab.Invoke(current, ["about:blank", true, false, false])!;
                    Assert(Views(current!).Count == 11 && Views(current!).All(view => view.CoreWebView2 is null), "additional blank tabs remain lazy");
                    var navigate = type.GetMethod("Navigate", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    navigate.Invoke(current, [$"http://127.0.0.1:{port}/one"]);
                    navigate.Invoke(current, [$"http://127.0.0.1:{port}/two"]);
                    var loaded = await WaitForTitle(current!, "Second page");
                    Assert(!page.Visible && Views(current!).Count(view => view.CoreWebView2 != null) == 1 && loaded.Visible, "latest navigation initializes only its selected tab");
                    var rail = (FlowLayoutPanel)type.GetField("rail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                    ((Button)rail.Controls[0]).PerformClick();
                    await WaitUntil(() => page.Visible && current!.Text.StartsWith("新建标签页"), "switching back restores native blank tab");
                    var activeTab = type.GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                    var blankView = (WebView2)activeTab.GetType().GetProperty("View")!.GetValue(activeTab)!;
                    FindControl<Button>(current!).Single(button => button.AccessibleName == "下载").PerformClick();
                    await WaitUntil(() => blankView.CoreWebView2?.IsDefaultDownloadDialogOpen == true, "downloads opened from a blank tab");
                    Assert(blankView.Visible && !page.Visible, "download dialog is visible above the blank tab");
                    blankView.CoreWebView2.CloseDefaultDownloadDialog();
                    await WaitUntil(() => page.Visible && !blankView.Visible, "closing downloads restores the blank tab");
                    var browserFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    loaded.CoreWebView2.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited) browserFailed.TrySetResult(true); };
                    var crash = loaded.CoreWebView2.CallDevToolsProtocolMethodAsync("Browser.crash", "{}");
                    _ = crash.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    Assert(await browserFailed.Task.WaitAsync(TimeSpan.FromSeconds(20)), "isolated browser exits while a blank tab is active");
                    var recover = FindControl<Button>(current!).Single(button => button.AccessibleName == "恢复浏览器");
                    await WaitUntil(() => recover.Visible && recover.Enabled, "recovery available while a blank tab is active");
                    recover.PerformClick();
                    await WaitUntil(() => page.Visible && !recover.Visible && current!.Text.StartsWith("新建标签页"), "recovery restores the selected native blank tab");
                    Console.WriteLine("PASS native blank tabs stay lazy, navigate on demand, and expose downloads");
                    Console.WriteLine("PASS browser-process recovery returns to the selected native blank tab");
                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                finally { current?.Close(); }
            };
            Application.Run(current);
            if (status == 0)
            {
                current = new BrowserWindow([]);
                current.Shown += async (_, _) => {
                    try {
                        var restored = typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!;
                        await WaitUntil(() => (bool)restored.GetValue(current!)! && Views(current!).Count == 11 && current!.Text.StartsWith("新建标签页"), "blank-tab session restored");
                        Assert(Views(current!).All(view => view.CoreWebView2 is null), "restoring a selected blank tab does not initialize a page");
                        Console.WriteLine("PASS a session selected on a blank tab restores without starting a WebView2 page");
                    } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                    finally { current?.Close(); }
                };
                Application.Run(current);
            }
        }
        stop.Cancel(); server.Stop();
        return status;
    }
    static int RunExtensionSmoke(string extensionPath)
    {
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", root);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(root, "no-edge"));
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--proxy-server=http://127.0.0.1:9");
        InitializeTestApplication();
        current = new BrowserWindow([]);
        current.Shown += async (_, _) => {
            try {
                await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!, "extension fixture started");
                var method = typeof(BrowserWindow).GetMethod("EnsureActiveCore", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var task = (Task<Microsoft.Web.WebView2.Core.CoreWebView2?>)method.Invoke(current, [])!;
                var core = await task ?? throw new Exception("extension fixture could not initialize WebView2");
                var extension = await core.Profile.AddBrowserExtensionAsync(Path.GetFullPath(extensionPath));
                string? expected = Environment.GetEnvironmentVariable("AGB_TEST_EXTENSION_ID");
                Assert(extension.IsEnabled && (string.IsNullOrEmpty(expected) || extension.Id == expected), "extension installed with expected identity");
                Assert((await core.Profile.GetBrowserExtensionsAsync()).Any(item => item.Id == extension.Id && item.IsEnabled), "extension is listed as enabled");
                Console.WriteLine("PASS isolated WebView2 loaded the supplied extension with its expected identity");
                if (Environment.GetEnvironmentVariable("AGB_TEST_EXTENSION_POPUP") is { Length: > 0 } popup)
                {
                    var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    string target = $"chrome-extension://{extension.Id}/{popup}"; ulong navigationId = 0;
                    core.NavigationStarting += (_, e) => { if (e.Uri == target) navigationId = e.NavigationId; };
                    core.NavigationCompleted += (_, e) => { if (navigationId != 0 && e.NavigationId == navigationId) navigation.TrySetResult(e.IsSuccess); };
                    core.Navigate(target);
                    Assert(await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15)), "extension popup page navigation succeeds");
                    string runtimeId = await core.ExecuteScriptAsync("chrome.runtime && chrome.runtime.id");
                    Assert(runtimeId == System.Text.Json.JsonSerializer.Serialize(extension.Id), "extension popup has extension runtime identity");
                    Console.WriteLine("PASS isolated WebView2 navigated to the extension popup with its runtime identity");
                    if (Environment.GetEnvironmentVariable("AGB_TEST_EXTENSION_POPUP_PROBE") == "1")
                    {
                        string query = "new Promise(resolve => chrome.tabs.query({active:true,currentWindow:true}, tabs => resolve(tabs.map(t => ({id:t.id,url:t.url})))) )";
                        string parameters = System.Text.Json.JsonSerializer.Serialize(new { expression = query, awaitPromise = true, returnByValue = true });
                        Console.WriteLine("Extension active-tab probe: " + await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters));
                    }
                }
            } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
            finally { current?.Close(); }
        };
        Application.Run(current);
        return status;
    }
    static int RunStoreLive(string id)
    {
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", root);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(root, "no-edge"));
        // Keep the installed test extension from sending network requests; the
        // store downloader itself uses HttpClient and the configured proxy.
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--proxy-server=http://127.0.0.1:9");
        InitializeTestApplication();
        var host = new Form { ShowInTaskbar = false };
        var view = new WebView2 { Dock = DockStyle.Fill };
        host.Controls.Add(view);
        _ = host.Handle; _ = view.Handle;
        var context = new ApplicationContext();
        var start = new System.Windows.Forms.Timer { Interval = 10 };
        start.Tick += async (_, _) => {
            start.Stop();
            try {
                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(root, "profile"), options: options).WaitAsync(TimeSpan.FromSeconds(25));
                await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(25));
                var core = view.CoreWebView2 ?? throw new Exception("WebView2 did not initialize");
                Console.WriteLine("PASS hidden WebView2 controller initialized without activating a window");
                string version = System.Text.RegularExpressions.Regex.Match(core.Settings.UserAgent, @"Chrome/(\d+(?:\.\d+){0,3})").Groups[1].Value;
                var package = await ChromeStoreExtensions.DownloadAsync(id, version, "", CancellationToken.None);
                if (id == "aapbdbdomjkkjkaonfhkkikfgjllcleb")
                {
                    Assert(package.Permissions.Contains("内容脚本访问：<all_urls>"), "real store package exposes content-script site access");
                    Assert(!package.Name.StartsWith("__MSG_", StringComparison.Ordinal), "real store package name is localized");
                }
                bool prompted = false;
                var denied = await ChromeStoreInstallation.InstallVerifiedAsync(package, root, core.Profile,
                    (_, replacing) => { prompted = true; Assert(!replacing, "first install is not an update"); return false; }, _ => { }, CancellationToken.None);
                string packageDirectory = Path.Combine(root, "store-extensions", id);
                Assert(prompted && !denied.Installed && !Directory.Exists(packageDirectory), "declining confirmation does not extract or install");

                using var cancelled = new CancellationTokenSource();
                bool cancellationObserved = false;
                try
                {
                    await ChromeStoreInstallation.InstallVerifiedAsync(package, root, core.Profile, (_, _) => true,
                        message => { if (message == "正在注册插件…") cancelled.Cancel(); }, cancelled.Token);
                }
                catch (OperationCanceledException) { cancellationObserved = true; }
                Assert(cancellationObserved && (!Directory.Exists(packageDirectory) || !Directory.EnumerateDirectories(packageDirectory).Any()), "cancelled install discards only its unpacked directory");
                Assert(!(await core.Profile.GetBrowserExtensionsAsync()).Any(item => item.Id == id), "cancelled install does not register the extension");

                var installed = await ChromeStoreInstallation.InstallVerifiedAsync(package, root, core.Profile,
                    (_, replacing) => { Assert(!replacing, "accepted first install is not an update"); return true; }, _ => { }, CancellationToken.None);
                Assert(installed.Installed && (await core.Profile.GetBrowserExtensionsAsync()).Any(item => item.Id == id && item.IsEnabled), "downloaded store extension has the requested identity");
                var deniedUpdate = await ChromeStoreInstallation.InstallVerifiedAsync(package, root, core.Profile,
                    (_, replacing) => { Assert(replacing, "installed extension is identified as an update"); return false; }, _ => { }, CancellationToken.None);
                Assert(!deniedUpdate.Installed && (await core.Profile.GetBrowserExtensionsAsync()).Any(item => item.Id == id && item.IsEnabled), "declined update leaves installed extension enabled");
                Console.WriteLine("PASS official Chrome Store download, signatures, confirmation, cancellation and isolated WebView2 installation");

                var fixture = ChromeStoreTests.CreateSignedFixture();
                var fixtureInstall = await ChromeStoreInstallation.InstallVerifiedAsync(fixture, root, core.Profile, (_, _) => true, _ => { }, CancellationToken.None);
                Assert(fixtureInstall.Installed, "signed test extension installed");
                string siteDirectory = Path.Combine(root, "extension-test-site"); Directory.CreateDirectory(siteDirectory);
                File.WriteAllText(Path.Combine(siteDirectory, "index.html"), "<!doctype html><html><body>Local extension fixture</body></html>");
                core.SetVirtualHostNameToFolderMapping("agbrowser-fixture.test", siteDirectory, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
                var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, e) => navigation.TrySetResult(e.IsSuccess);
                core.Navigate("https://agbrowser-fixture.test/index.html");
                Assert(await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15)), "local extension test page loaded");
                await WaitForScriptResult(view, "document.body?.dataset.agbFixture", "\"loaded\"");
                Console.WriteLine("PASS isolated WebView2 runs an installed test extension content script on a local page");
            } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
            finally { start.Dispose(); view.Dispose(); host.Dispose(); context.ExitThread(); }
        };
        start.Start();
        Application.Run(context);
        return status;
    }
    static int RunStartupBenchmarkChild()
    {
        try
        {
            int expectedTabs = int.Parse(Environment.GetEnvironmentVariable("AGB_STARTUP_BENCH_TABS") ?? "1");
            int expectedSelected = int.Parse(Environment.GetEnvironmentVariable("AGB_STARTUP_BENCH_SELECTED") ?? "0");
            string? external = Environment.GetEnvironmentVariable("AGB_STARTUP_BENCH_EXTERNAL");
            InitializeTestApplication();
            current = new BrowserWindow(string.IsNullOrEmpty(external) ? [] : [external]);
            current.Shown += async (_, _) => {
                try {
                    await WaitUntil(() => (bool)typeof(BrowserWindow).GetField("sessionRestored", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)! && Views(current!).Count == expectedTabs, "benchmark session restored");
                    var page = await WaitForTitle(current!, "Startup fixture");
                    Assert(new Uri(page.CoreWebView2.Source).Query == (string.IsNullOrEmpty(external) ? "?tab=" + expectedSelected : "?tab=external"), "benchmark restores the requested active tab");
                    var tabList = (System.Collections.IEnumerable)typeof(BrowserWindow).GetField("tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                    var rail = (FlowLayoutPanel)typeof(BrowserWindow).GetField("rail", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(current!)!;
                    int restoredIndex = 0;
                    foreach (var tab in tabList)
                    {
                        string url = (string)tab!.GetType().GetProperty("Url")!.GetValue(tab)!;
                        var button = (Control)tab.GetType().GetProperty("Button")!.GetValue(tab)!;
                        string expectedQuery = !string.IsNullOrEmpty(external) && restoredIndex == expectedTabs - 1 ? "?tab=external" : "?tab=" + restoredIndex;
                        Assert(new Uri(url).Query == expectedQuery && rail.Controls.GetChildIndex(button) == restoredIndex, "benchmark preserves tab and rail order");
                        restoredIndex++;
                    }
                    await WaitForScriptResult(page, "document.querySelector('h1').textContent", "\"Startup fixture ready\"");
                    Assert(Views(current!).Count(v => v.CoreWebView2 != null) == 1, "benchmark initializes only one tab");
                    if (expectedTabs > 1)
                    {
                        Assert(rail.Controls.OfType<Button>().Count(b => b.Image != null) >= expectedTabs - 1, "benchmark restores inactive favicon cache");
                    }
                    Console.WriteLine("AGB_BENCH_READY");
                } catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
                finally { current?.Close(); }
            };
            Application.Run(current);
            return status;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void VerifyRealEdgeRead(string fixturePath)
    {
        try
        {
            Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", null);
            var type = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.EdgeFavoritesReader")!;
            var reader = Activator.CreateInstance(type)!;
            var task = (Task)type.GetMethod("ReadIfChangedAsync")!.Invoke(reader, null)!;
            task.GetAwaiter().GetResult();
            object? snapshot = task.GetType().GetProperty("Result")!.GetValue(task);
            Assert(snapshot != null, "real Edge favorites profile could not be read");
            var bar = (System.Collections.ICollection)snapshot!.GetType().GetProperty("Bar")!.GetValue(snapshot)!;
            Console.WriteLine($"PASS real Edge current profile read-only: {bar.Count} top-level favorites");
        }
        finally { Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", fixturePath); }
    }
    static void VerifyAddressNormalization()
    {
        var normalize = typeof(BrowserWindow).GetMethod("Normalize", BindingFlags.Static | BindingFlags.NonPublic)!;
        string Convert(string value) => (string)normalize.Invoke(null, [value])!;
        Assert(Convert("localhost:8080") == "http://localhost:8080/", "localhost uses HTTP");
        Assert(Convert("127.0.0.1:8080") == "http://127.0.0.1:8080/", "loopback IP uses HTTP");
        Assert(Convert("localhost.example.com") == "https://localhost.example.com/", "localhost prefix does not disable HTTPS");
        Assert(Convert("127.0.0.1.example.com") == "https://127.0.0.1.example.com/", "loopback prefix does not disable HTTPS");
        Assert(Convert("[::1]:8080") == "http://[::1]:8080/", "IPv6 loopback is recognized");
        Assert(Convert("192.168.1.1:8080/admin") == "http://192.168.1.1:8080/admin", "private IPv4 uses HTTP");
        Assert(Convert("10.2.3.4") == "http://10.2.3.4/", "10/8 uses HTTP");
        Assert(Convert("172.16.0.1") == "http://172.16.0.1/" && Convert("172.31.255.255") == "http://172.31.255.255/", "172.16/12 uses HTTP");
        Assert(Convert("172.15.0.1") == "https://172.15.0.1/" && Convert("172.32.0.1") == "https://172.32.0.1/", "172.16/12 boundaries do not include public IPs");
        Assert(Convert("[fd12::1]:8080") == "http://[fd12::1]:8080/", "IPv6 unique-local uses HTTP");
        Assert(Convert("192.168.1.1.example.com") == "https://192.168.1.1.example.com/", "IP-looking hostname keeps HTTPS");
        Assert(Convert("https://192.168.1.1/") == "https://192.168.1.1/", "explicit private HTTPS is preserved");
        Console.WriteLine("PASS address normalization handles loopback and private IPs without changing explicit protocols");
    }
    static void VerifyDebugPortCleanupError()
    {
        string fixture = Path.Combine(root, "unremovable-debug-port");
        var type = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.BrowserNetworkSettings")!;
        var clear = type.GetMethod("ClearStaleDebugPort")!;
        Assert(clear.Invoke(null, [fixture]) is null, "missing first-run directory is not reported as a debug-port error");
        string portPath = Path.Combine(fixture, "profile", "EBWebView", "DevToolsActivePort");
        Directory.CreateDirectory(portPath);
        string sentinel = Path.Combine(portPath, "keep"); File.WriteAllText(sentinel, "preserve");
        string? warning = (string?)clear.Invoke(null, [fixture]);
        Assert(!string.IsNullOrWhiteSpace(warning) && File.ReadAllText(sentinel) == "preserve", "debug-port cleanup failure is reported without touching other data");
        Console.WriteLine("PASS inaccessible debug-port cleanup reports a warning and preserves data");
    }
    static async Task<int> VerifyRealFavoriteIconCoverage()
    {
        var snapshot = await new EdgeFavoritesReader().ReadIfChangedAsync();
        if (snapshot is null || !snapshot.Available) { Console.Error.WriteLine("Edge favorite profile is unavailable"); return 1; }
        IEnumerable<FavoriteNode> Walk(IEnumerable<FavoriteNode> nodes)
        {
            foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        int total = Walk(snapshot.EnumerateRoots()).Select(node => node.Url).OfType<string>().Distinct(StringComparer.Ordinal).Count();
        var watch = Stopwatch.StartNew();
        var icons = FavoriteIcons.Read(snapshot, CancellationToken.None);
        Console.WriteLine($"PASS read-only Edge favicon coverage: {icons.Count}/{total} distinct bookmark URLs in {watch.ElapsedMilliseconds} ms; no titles or URLs displayed");
        return 0;
    }
    static async Task VerifyNativeBridge(BrowserWindow window, int port)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "AgBrowser.Desktop.exe");
        var settings = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        settings.ArgumentList.Add("chrome-extension://fjkcppekpigobilckiggnjdfeiiabmee/");
        using var host = Process.Start(settings) ?? throw new Exception("native host did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            object Tree(int sequence, object[] children) => new { type = "snapshot", sequence, tree = new[] { new { id = "0", children = new[] { new { id = "1", title = "Favorites bar", children } } } } };
            await WriteNative(host.StandardInput.BaseStream, Tree(1, [new { id = "edge-1", title = "Connected Edge favorite", url = $"http://127.0.0.1:{port}/one" }]), timeout.Token);
            await Task.Delay(1200);
            if (host.HasExited) throw new Exception("native host exited: " + host.ExitCode + " " + await host.StandardError.ReadToEndAsync());
            await WaitUntil(() => FavoriteButton(window, "Connected Edge favorite") != null, "native host snapshot shown");
            var add = FindControl<Button>(window).Single(b => b.AccessibleName == "添加到 Edge 收藏栏");
            Assert(add.Enabled, "favorite write enabled after bridge snapshot");
            var activeView = Views(window).FirstOrDefault(v => v.Visible && v.CoreWebView2 != null)?.CoreWebView2;
            Console.WriteLine($"Bridge fixture active page: [{activeView?.DocumentTitle}] [{activeView?.Source}]");
            add.PerformClick();
            using var request = await ReadNative(host.StandardOutput.BaseStream, timeout.Token);
            Assert(request.RootElement.GetProperty("op").GetString() == "create", "favorite create forwarded");
            string id = request.RootElement.GetProperty("id").GetString()!;
            Assert(request.RootElement.GetProperty("parentId").GetString() == "1", "correct Edge root");
            await WriteNative(host.StandardInput.BaseStream, new { type = "result", id, ok = true }, timeout.Token);
            await WriteNative(host.StandardInput.BaseStream, Tree(2, [new { id = "edge-2", title = "Created from agbrowser", url = $"http://127.0.0.1:{port}/one" }]), timeout.Token);
            await WaitUntil(() => FavoriteButton(window, "Created from agbrowser") != null, "created favorite reflected in UI");
            Console.WriteLine("PASS native messaging bridge and favorite-create round trip with simulated Edge");
            var bridge = typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var execute = bridge.GetType().GetMethod("ExecuteAsync")!;
            long generation = (long)bridge.GetType().GetProperty("CurrentGeneration")!.GetValue(bridge)!;
            var uncertain = (Task)execute.Invoke(bridge, [generation, "create", "1", null, "Uncertain result", $"http://127.0.0.1:{port}/one"])!;
            using (var inFlight = await ReadNative(host.StandardOutput.BaseStream, timeout.Token))
                Assert(inFlight.RootElement.GetProperty("title").GetString() == "Uncertain result", "request sent before disconnect");
            host.StandardInput.Close();
            try { await uncertain.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("disconnected request unexpectedly succeeded"); }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("可能已在 Edge 完成") && ex.Message.Contains("先检查收藏"), "uncertain result explains safe retry"); }
            Console.WriteLine("PASS in-flight favorite operation reports an uncertain result after disconnect");
        }
        finally
        {
            host.StandardInput.Close();
            try { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { host.Kill(); }
        }
    }
    static async Task VerifyWorkerBridge(BrowserWindow window, int port)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "AgBrowser.Desktop.exe");
        string fixture = Path.GetFullPath("packages/extensions/agb-edge-favorites/test/bridge-fixture.mjs");
        var settings = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        settings.ArgumentList.Add(fixture); settings.ArgumentList.Add(executable); settings.ArgumentList.Add($"http://127.0.0.1:{port}/one");
        using var process = Process.Start(settings) ?? throw new Exception("worker bridge fixture did not start");
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<System.Text.Json.JsonDocument> Command(object command)
        {
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(command));
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new Exception("worker fixture exited: " + await errors);
            var response = System.Text.Json.JsonDocument.Parse(line);
            if (!response.RootElement.GetProperty("ok").GetBoolean()) throw new Exception("worker fixture command failed");
            return response;
        }
        static System.Text.Json.JsonElement Bar(System.Text.Json.JsonDocument state) => state.RootElement.GetProperty("tree").GetProperty("children")[0].GetProperty("children");
        var bridge = typeof(BrowserWindow).GetField("edgeBridge", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var execute = bridge.GetType().GetMethod("ExecuteAsync")!;
        long Generation() => (long)bridge.GetType().GetProperty("CurrentGeneration")!.GetValue(bridge)!;
        async Task Execute(string op, string? parent = null, string? id = null, string? title = null, string? url = null) =>
            await (Task)execute.Invoke(bridge, [Generation(), op, parent, id, title, url])!;
        try
        {
            var edgeBridge = (EdgeFavoritesBridge)bridge;
            await WaitUntil(() => edgeBridge.GetConnections().Any(choice => choice.Preview == "Connected Edge favorite"), "production worker profile available");
            var choice = edgeBridge.GetConnections().Single(item => item.Preview == "Connected Edge favorite");
            if (!choice.Selected) edgeBridge.SelectConnection(choice.Generation);
            await WaitUntil(() => FavoriteButton(window, "Connected Edge favorite") != null, "production worker snapshot shown");
            await WaitUntil(() => FavoriteFolder(window, "其他收藏夹") != null, "empty Other favorites root shown while connected");
            var snapshot = typeof(BrowserWindow).GetField("bridgeSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var otherRoot = snapshot.GetType().GetProperty("Other")!.GetValue(snapshot)!;
            using (var rootMenu = (ContextMenuStrip)typeof(BrowserWindow).GetMethod("CreateFavoriteFolderMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [otherRoot, true, Generation()])!)
            {
                Assert(rootMenu.Items.Cast<ToolStripItem>().Select(x => x.Text).SequenceEqual(["收藏当前页面到此文件夹", "新建子文件夹"]), "Other favorites root cannot be renamed or deleted");
            }
            await Execute("create", parent: "2", title: "First other favorite", url: $"http://127.0.0.1:{port}/one");
            string otherId;
            using (var state = await Command(new { op = "state" }))
                otherId = state.RootElement.GetProperty("tree").GetProperty("children")[1].GetProperty("children")[0].GetProperty("id").GetString()!;
            await WaitUntil(() => {
                var rootFolder = FavoriteFolder(window, "其他收藏夹");
                if (rootFolder is null) return false;
                rootFolder.ShowDropDown();
                bool found = rootFolder.DropDownItems.Cast<ToolStripItem>().Any(x => x.Text == "First other favorite");
                rootFolder.HideDropDown();
                return found;
            }, "first favorite under Other favorites shown");
            await Execute("remove", id: otherId);
            Console.WriteLine("PASS empty Other favorites root, protected actions and first item through simulated Edge");
            using (await Command(new { op = "edge-create", title = "Added in simulated Edge", url = $"http://127.0.0.1:{port}/two" })) { }
            await WaitUntil(() => FavoriteButton(window, "Added in simulated Edge") != null, "Edge-side create displayed");
            using (await Command(new { op = "edge-update", title = "Edited in simulated Edge" })) { }
            await WaitUntil(() => FavoriteButton(window, "Edited in simulated Edge") != null, "Edge-side update displayed");
            using (await Command(new { op = "edge-remove" })) { }
            await WaitUntil(() => FavoriteButton(window, "Edited in simulated Edge") is null, "Edge-side removal displayed");

            var add = FindControl<Button>(window).Single(b => b.AccessibleName == "添加到 Edge 收藏栏");
            Assert(add.Enabled, "favorite add enabled for production worker"); add.PerformClick();
            try { await WaitUntil(() => FavoriteButton(window, "First page") != null, "app-side create appears in worker snapshot"); }
            catch (Exception ex)
            {
                using var state = await Command(new { op = "state" });
                var visible = Views(window).FirstOrDefault(v => v.Visible && v.CoreWebView2 != null)?.CoreWebView2;
                throw new Exception($"Worker fixture state={state.RootElement.GetRawText()}, active title={visible?.DocumentTitle}, active source={visible?.Source}, add enabled={add.Enabled}", ex);
            }
            string id;
            using (var state = await Command(new { op = "state" }))
            {
                var item = Bar(state).EnumerateArray().Single(x => x.GetProperty("title").GetString() == "First page");
                id = item.GetProperty("id").GetString()!;
                Assert(item.GetProperty("url").GetString()!.EndsWith("/one", StringComparison.Ordinal), "app-side URL reached worker bookmarks API");
            }
            await Execute("update", id: id, title: "Edited from agbrowser", url: $"http://127.0.0.1:{port}/two");
            await WaitUntil(() => FavoriteButton(window, "Edited from agbrowser") != null, "app-side update reflected");
            await Execute("remove", id: id);
            await WaitUntil(() => FavoriteButton(window, "Edited from agbrowser") is null, "app-side removal reflected");
            await Execute("create", parent: "1", title: "同步文件夹");
            string folderId;
            using (var state = await Command(new { op = "state" }))
                folderId = Bar(state).EnumerateArray().Single(x => x.GetProperty("title").GetString() == "同步文件夹").GetProperty("id").GetString()!;
            await Execute("create", parent: folderId, title: "Nested favorite", url: $"http://127.0.0.1:{port}/one");
            using (var state = await Command(new { op = "state" }))
                Assert(Bar(state).EnumerateArray().Single(x => x.GetProperty("id").GetString() == folderId).GetProperty("children").GetArrayLength() == 1, "nested favorite created");
            await Execute("removeTree", id: folderId);
            using (var state = await Command(new { op = "state" }))
                Assert(!Bar(state).EnumerateArray().Any(x => x.GetProperty("id").GetString() == folderId), "folder tree removed");
            Console.WriteLine("PASS actual desktop bridge and extension worker with simulated Edge bookmark operations in both directions");
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { process.Kill(); }
            if (process.HasExited && process.ExitCode != 0) Console.Error.WriteLine("worker fixture: " + await errors);
        }
    }
    static async Task WriteNative(Stream stream, object message, CancellationToken cancel)
    {
        byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message); byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancel); await stream.WriteAsync(bytes, cancel); await stream.FlushAsync(cancel);
    }
    static async Task<System.Text.Json.JsonDocument> ReadNative(Stream stream, CancellationToken cancel)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancel); int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > 1024 * 1024) throw new Exception("invalid native host frame");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancel); return System.Text.Json.JsonDocument.Parse(bytes);
    }
    static void WriteEdgeFixture(string edgeDir, int port, bool changed)
    {
        var items = new List<object> { new { id = "1", name = "Fixture favorite", type = "url", url = $"http://127.0.0.1:{port}/two" } };
        if (changed) items.Add(new { id = "2", name = "Added in Edge", type = "url", url = $"http://127.0.0.1:{port}/one" });
        string json = System.Text.Json.JsonSerializer.Serialize(new { roots = new { bookmark_bar = new { id = "10", type = "folder", children = items }, other = new { id = "11", type = "folder", children = Array.Empty<object>() } } });
        File.WriteAllText(Path.Combine(edgeDir, "Default", "Bookmarks"), json);
    }
    static ToolStripButton? FavoriteButton(Control root, string text) => FindControl<ToolStrip>(root).SelectMany(t => t.Items.OfType<ToolStripButton>()).FirstOrDefault(x => x.Text == text || x.ToolTipText == text);
    static ToolStripDropDownButton? FavoriteFolder(Control root, string text) => FindControl<ToolStrip>(root).SelectMany(t => t.Items.OfType<ToolStripDropDownButton>()).FirstOrDefault(x => x.ToolTipText == text);
    static void VerifySingleInstance()
    {
        var type = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.SingleInstance")!;
        var first = (IDisposable)Activator.CreateInstance(type, root)!;
        var second = (IDisposable)Activator.CreateInstance(type, root)!;
        try
        {
            var owner = type.GetProperty("IsOwner")!;
            Assert((bool)owner.GetValue(first)! && !(bool)owner.GetValue(second)!, "single profile owner");
            var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            type.GetMethod("Start")!.Invoke(first, [new Action<string[]>(args => received.TrySetResult(args))]);
            var sent = (Task)type.GetMethod("ForwardAsync")!.Invoke(second, [new[] { "--single-argument", "https://example.com/path" }])!;
            sent.GetAwaiter().GetResult();
            var args = received.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert(args.Length == 2 && args[1] == "https://example.com/path", "forwarded link");
            Console.WriteLine("PASS single instance and link forwarding");
        }
        finally { second.Dispose(); first.Dispose(); }
    }
    static IEnumerable<Control> Descendants(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var child in Descendants(c)) yield return child; } }
    static IEnumerable<T> FindControl<T>(Control root) where T : Control => Descendants(root).OfType<T>();
    static Button? FindButton(Control root, string name) => FindControl<Button>(root).FirstOrDefault(b => b.AccessibleName == name || b.Text == name);
    static List<WebView2> Views(Control root) => FindControl<WebView2>(root).ToList();
    static bool SessionMatches(string path, int count, int activeIndex, string suffix)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var urls = root.GetProperty("urls");
            return urls.GetArrayLength() == count && root.GetProperty("activeIndex").GetInt32() == activeIndex && urls[activeIndex].GetString()!.EndsWith(suffix, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException) { return false; }
    }
    static async Task WaitUntil(Func<bool> test, string label) { for (int i = 0; i < 200; i++) { if (test()) return; await Task.Delay(100); } throw new Exception("Timed out: " + label); }
    static async Task<WebView2> WaitForTitle(BrowserWindow window, string title) { WebView2? found = null; await WaitUntil(() => { found = Views(window).FirstOrDefault(v => v.CoreWebView2?.DocumentTitle == title); return found != null; }, title); return found!; }
    static async Task WaitForScriptResult(WebView2 view, string expression, string expected)
    {
        for (int i = 0; i < 100; i++)
        {
            try { if (await view.CoreWebView2.ExecuteScriptAsync(expression) == expected) return; } catch { }
            await Task.Delay(100);
        }
        throw new Exception("Timed out waiting for script result: " + expected);
    }
    static void Assert(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); }
}
