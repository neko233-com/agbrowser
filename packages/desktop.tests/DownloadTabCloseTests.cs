using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgBrowser.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class DownloadTabCloseTests
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int PayloadSize = 512 * 1024;
    private const int BlobSize = 32 * 1024 * 1024;
    private const int ParallelPayloadSize = 64 * 1024 * 1024;

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(directory, "no-edge"));
        string requestedMode = Environment.GetEnvironmentVariable("AGB_TEST_DOWNLOAD_MODE") ?? "complete";
        int parallelCount = int.TryParse(Environment.GetEnvironmentVariable("AGB_TEST_PARALLEL_COUNT"), out int configured) ? configured : 4;
        bool noRange = Environment.GetEnvironmentVariable("AGB_TEST_PARALLEL_NO_RANGE") == "1";
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", requestedMode == "parallel" ? null : "--no-proxy-server");
        if (requestedMode == "parallel") new BrowserDownloadSettings(parallelCount).Save(directory);
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        using var stop = new CancellationTokenSource();
        var parallelOffsets = new ConcurrentBag<int>();
        int unauthorizedRequests = 0;
        long streamedBytes = 0;
        _ = Task.Run(async () => {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext request;
                try { request = await server.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                _ = Task.Run(async () => {
                    try
                    {
                        if (request.Request.Url!.AbsolutePath == "/slow")
                        {
                            int size = requestedMode == "parallel" ? ParallelPayloadSize : request.Request.QueryString["second"] == "1" ? PayloadSize * 2 : requestedMode.StartsWith("beforeunload", StringComparison.Ordinal) ? PayloadSize * 4 : requestedMode == "dialog" ? PayloadSize * 2 : PayloadSize;
                            if (requestedMode == "parallel" && !string.Equals(request.Request.Cookies["agbfixture"]?.Value, "yes", StringComparison.Ordinal))
                            {
                                Interlocked.Increment(ref unauthorizedRequests); request.Response.StatusCode = 403; request.Response.Close(); return;
                            }
                            request.Response.ContentType = "text/plain";
                            request.Response.AddHeader("Content-Disposition", "attachment; filename=fixture.txt");
                            request.Response.AddHeader("Accept-Ranges", noRange ? "none" : "bytes");
                            request.Response.AddHeader("ETag", "\"agb-download-fixture\"");
                            int from = 0;
                            string? range = request.Request.Headers["Range"];
                            if (range is not null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(range[6..].TrimEnd('-'), out var offset) && offset >= 0 && offset < size)
                            {
                                from = offset;
                                if (requestedMode == "parallel") parallelOffsets.Add(offset);
                                request.Response.StatusCode = 206;
                                request.Response.AddHeader("Content-Range", $"bytes {from}-{size - 1}/{size}");
                            }
                            request.Response.ContentLength64 = size - from;
                            var chunk = new byte[requestedMode == "parallel" ? 65536 : 8192]; Array.Fill(chunk, (byte)0x5a);
                            for (int sent = from; sent < size; sent += chunk.Length)
                            {
                                int count = Math.Min(chunk.Length, size - sent);
                                if (requestedMode == "parallel")
                                    for (int i = 0; i < count; i++) chunk[i] = (byte)((sent + i) % 251);
                                await request.Response.OutputStream.WriteAsync(chunk.AsMemory(0, count), stop.Token);
                                await request.Response.OutputStream.FlushAsync(stop.Token);
                                Interlocked.Add(ref streamedBytes, count);
                                await Task.Delay(requestedMode == "parallel" ? 10 : 40, stop.Token);
                            }
                        }
                        else
                        {
                            byte[] content = System.Text.Encoding.UTF8.GetBytes("<!doctype html><title>Download fixture</title><p>local fixture</p>");
                            request.Response.ContentType = "text/html"; request.Response.ContentLength64 = content.Length;
                            await request.Response.OutputStream.WriteAsync(content, stop.Token);
                        }
                        request.Response.Close();
                    }
                    catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
                });
            }
        });
        Program.InitializeTestApplication();
        using var browser = new BrowserWindow([$"http://127.0.0.1:{port}/page"]);
        _ = browser.Handle;
        using var context = new ApplicationContext();
        using var start = new System.Windows.Forms.Timer { Interval = 10 };
        int status = 0;
        start.Tick += async (_, _) => {
            start.Stop();
            try
            {
                Type type = typeof(BrowserWindow);
                using (var prompt = (Form)type.GetMethod("CreateDownloadCloseDialog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [2])!)
                {
                    if (!ReferenceEquals(prompt.AcceptButton, prompt.CancelButton) || ((Button)prompt.CancelButton!).Text != "保留浏览器" || ((Button)prompt.CancelButton!).DialogResult != DialogResult.No)
                        throw new InvalidOperationException("Download warning does not default to keeping the browser open");
                }
                await ((Task)type.GetMethod("StartBrowser", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(30));
                var tabs = (IList)type.GetField("tabs", PrivateInstance)!.GetValue(browser)!;
                if (tabs.Count != 1) throw new InvalidOperationException("Expected a single fixture tab");
                object original = tabs[0]!;
                var view = (WebView2)original.GetType().GetProperty("View")!.GetValue(original)!;
                await WaitUntil(() => view.CoreWebView2?.DocumentTitle == "Download fixture", "local page");
                string mode = requestedMode;
                bool keepTab = Environment.GetEnvironmentVariable("AGB_TEST_KEEP_DOWNLOAD_TAB") == "1" || mode == "suspend";
                if (mode == "parallel")
                {
                    keepTab = true;
                    await view.CoreWebView2.ExecuteScriptAsync("document.cookie='agbfixture=yes; path=/; SameSite=Lax'");
                }
                if ((mode is "blob" or "low-memory") && !keepTab)
                    await ((Task)type.GetMethod("AddTab", PrivateInstance)!.Invoke(browser, ["about:blank", true, false, false])!).WaitAsync(TimeSpan.FromSeconds(10));
                if (mode == "low-memory")
                {
                    await WaitUntil(() => !(bool)original.GetType().GetProperty("IsNavigating")!.GetValue(original)!, "fixture navigation completes before memory policy");
                    long inactive = Stopwatch.GetTimestamp() - (long)(TimeSpan.FromMinutes(3).TotalSeconds * Stopwatch.Frequency);
                    original.GetType().GetProperty("InactiveSince")!.SetValue(original, inactive);
                    type.GetMethod("ApplyBackgroundMemoryPolicy", PrivateInstance)!.Invoke(browser, []);
                    if (view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Low)
                        throw new InvalidOperationException("Fixture page did not enter low memory mode");
                }
                string resultPath = Path.Combine(directory, "fixture.txt");
                var started = new TaskCompletionSource<CoreWebView2DownloadOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                var secondStarted = new TaskCompletionSource<CoreWebView2DownloadOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.CoreWebView2.DownloadStarting += (_, e) => {
                    e.Handled = true;
                    bool second = e.DownloadOperation.Uri.EndsWith("?second=1", StringComparison.Ordinal);
                    e.ResultFilePath = second ? Path.Combine(directory, "fixture2.txt") : resultPath;
                    (second ? secondStarted : started).TrySetResult(e.DownloadOperation);
                };
                if (mode == "blob")
                    _ = view.CoreWebView2.ExecuteScriptAsync($"(() => {{ const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([new Uint8Array({BlobSize}).fill(90)], {{type: 'text/plain'}})); a.download = 'fixture.txt'; a.click(); }})()");
                else if (mode == "parallel")
                    _ = view.CoreWebView2.ExecuteScriptAsync($"(() => {{ const a = document.createElement('a'); a.href = 'http://127.0.0.1:{port}/slow'; a.download = 'fixture.txt'; document.body.append(a); a.click(); }})()");
                else view.CoreWebView2.Navigate($"http://127.0.0.1:{port}/slow");
                var download = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var terminal = new TaskCompletionSource<(CoreWebView2DownloadState State, bool CanResume, CoreWebView2DownloadInterruptReason Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
                download.StateChanged += (_, _) => {
                    if (download.State == CoreWebView2DownloadState.Completed || (download.State == CoreWebView2DownloadState.Interrupted && !download.CanResume))
                        terminal.TrySetResult((download.State, download.CanResume, download.InterruptReason));
                };
                int beforeUnloadDialogs = 0;
                bool acceptClose = mode != "beforeunload-cancel";
                if (mode.StartsWith("beforeunload", StringComparison.Ordinal))
                {
                    view.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                    view.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.javascriptDialogOpening").DevToolsProtocolEventReceived += (_, e) => {
                        using var json = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                        if (json.RootElement.GetProperty("type").GetString() != "beforeunload") return;
                        beforeUnloadDialogs++;
                        browser.BeginInvoke(async () => await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.handleJavaScriptDialog", acceptClose ? "{\"accept\":true}" : "{\"accept\":false}"));
                    };
                    await view.CoreWebView2.ExecuteScriptAsync("window.onbeforeunload = e => { e.preventDefault(); e.returnValue = 'unsaved'; return 'unsaved'; }");
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                    await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                    if (await view.CoreWebView2.ExecuteScriptAsync("navigator.userActivation.hasBeenActive") != "true") throw new InvalidOperationException("Fixture did not receive trusted activation");
                }
                CoreWebView2DownloadOperation? secondDownload = null;
                var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (mode == "multiple")
                {
                    view.CoreWebView2.Navigate($"http://127.0.0.1:{port}/slow?second=1");
                    secondDownload = await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    secondDownload.StateChanged += (_, _) => {
                        if (secondDownload.State == CoreWebView2DownloadState.Completed) secondCompleted.TrySetResult();
                        else if (secondDownload.State == CoreWebView2DownloadState.Interrupted && !secondDownload.CanResume)
                            secondCompleted.TrySetException(new InvalidOperationException($"Second download failed: {secondDownload.InterruptReason}"));
                    };
                }
                if (mode == "minimized-download")
                {
                    browser.WindowState = FormWindowState.Minimized;
                    typeof(Control).GetMethod("OnResize", PrivateInstance)!.Invoke(browser, [EventArgs.Empty]);
                    long inactive = Stopwatch.GetTimestamp() - (long)(TimeSpan.FromMinutes(3).TotalSeconds * Stopwatch.Frequency);
                    original.GetType().GetProperty("InactiveSince")!.SetValue(original, inactive);
                    type.GetMethod("ApplyBackgroundMemoryPolicy", PrivateInstance)!.Invoke(browser, []);
                    if (view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                        throw new InvalidOperationException("A minimized page with an active download entered low memory mode");
                    browser.WindowState = FormWindowState.Normal;
                    typeof(Control).GetMethod("OnResize", PrivateInstance)!.Invoke(browser, [EventArgs.Empty]);
                }
                Func<int> pendingCount = () => (int)type.GetMethod("PendingDownloadCount", PrivateInstance)!.Invoke(browser, [])!;
                int expectedPending = mode == "multiple" ? 2 : 1;
                if (mode != "blob" && pendingCount() != expectedPending) throw new InvalidOperationException("Active downloads were not tracked");
                var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (mode == "failure")
                    view.CoreWebView2.ProcessFailed += (_, e) => { if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) crashed.TrySetResult(); };
                if (!keepTab && mode is not ("blob" or "low-memory"))
                    await ((Task)type.GetMethod("AddTab", PrivateInstance)!.Invoke(browser, ["about:blank", true, false, false])!).WaitAsync(TimeSpan.FromSeconds(10));
                if (download.State != CoreWebView2DownloadState.InProgress) throw new InvalidOperationException("Fixture finished before the tab was closed");
                if (mode == "downloading")
                {
                    long inactive = Stopwatch.GetTimestamp() - (long)(TimeSpan.FromMinutes(3).TotalSeconds * Stopwatch.Frequency);
                    original.GetType().GetProperty("InactiveSince")!.SetValue(original, inactive);
                    type.GetMethod("ApplyBackgroundMemoryPolicy", PrivateInstance)!.Invoke(browser, []);
                    if (view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                        throw new InvalidOperationException("A page with an active download entered low memory mode");
                }
                if (mode == "pause")
                {
                    await WaitUntil(() => download.BytesReceived >= 16384, "download receives first bytes");
                    download.Pause();
                    await WaitUntil(() => download.State == CoreWebView2DownloadState.Interrupted, "download paused");
                    if (!download.CanResume) throw new InvalidOperationException($"Paused download cannot resume: {download.InterruptReason}");
                }
                if (mode == "suspend")
                {
                    view.Visible = false;
                    if (!await view.CoreWebView2.TrySuspendAsync()) throw new InvalidOperationException("Download page could not be suspended");
                }
                if (!keepTab)
                {
                    type.GetMethod("CloseTab", PrivateInstance)!.Invoke(browser, [original, true]);
                    if (mode == "beforeunload-cancel")
                    {
                        await WaitUntil(() => beforeUnloadDialogs == 1 && !(bool)original.GetType().GetProperty("ClosePending")!.GetValue(original)!, "canceled tab close completes");
                        if (!tabs.Contains(original) || view.IsDisposed || download.State != CoreWebView2DownloadState.InProgress)
                            throw new InvalidOperationException("Canceling close did not retain the page and active download");
                        acceptClose = true;
                        type.GetMethod("CloseTab", PrivateInstance)!.Invoke(browser, [original, true]);
                    }
                    await WaitUntil(() => !tabs.Contains(original), "download source tab closes after page confirmation");
                    if (mode == "low-memory")
                    {
                        await Task.Delay(200);
                        if (view.IsDisposed || view.CoreWebView2.IsSuspended) throw new InvalidOperationException("Previously managed page should not mix memory target and suspension");
                    }
                    else await WaitUntil(() => view.IsDisposed || view.CoreWebView2.IsSuspended, "closed download page suspended or released");
                    if (beforeUnloadDialogs != (mode == "beforeunload-cancel" ? 2 : mode == "beforeunload" ? 1 : 0)) throw new InvalidOperationException("Unexpected beforeunload confirmation count");
                    if (mode != "blob" && pendingCount() != expectedPending) throw new InvalidOperationException("Closing the tab lost track of its downloads");
                }
                if (mode == "failure")
                {
                    if (!Path.GetFullPath(view.CoreWebView2.Environment.UserDataFolder).Equals(Path.GetFullPath(Path.Combine(directory, "profile")), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Refusing to crash a browser outside the fixture profile");
                    var crash = view.CoreWebView2.CallDevToolsProtocolMethodAsync("Browser.crash", "{}");
                    _ = crash.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    await crashed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await WaitUntil(() => view.IsDisposed && pendingCount() == 0, "failed browser download resources released");
                    Console.WriteLine("PASS isolated WebView2 browser crash releases the closed download controller");
                    return;
                }
                if (mode == "dialog")
                {
                    await ((Task)type.GetMethod("OpenDownloads", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(10));
                    object current = tabs[0]!;
                    var currentView = (WebView2)current.GetType().GetProperty("View")!.GetValue(current)!;
                    await WaitUntil(() => currentView.CoreWebView2?.IsDefaultDownloadDialogOpen == true, "download list opens from the remaining tab");
                    currentView.CoreWebView2.CloseDefaultDownloadDialog();
                }
                if (mode == "pause")
                {
                    await Task.Delay(300);
                    if (view.IsDisposed) throw new InvalidOperationException("Paused download lost its controller");
                    download.Resume();
                }
                else if (mode == "cancel") download.Cancel();
                (CoreWebView2DownloadState State, bool CanResume, CoreWebView2DownloadInterruptReason Reason) outcome;
                try { outcome = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(mode == "parallel" ? 45 : mode.StartsWith("beforeunload", StringComparison.Ordinal) ? 16 : 8)); }
                catch (TimeoutException)
                {
                    string state;
                    try { state = $"{download.State}, reason={download.InterruptReason}, bytes={download.BytesReceived}/{download.TotalBytesToReceive}"; }
                    catch (Exception ex) { state = ex.GetType().Name; }
                    string files = string.Join(", ", Directory.EnumerateFiles(directory, "fixture*", SearchOption.TopDirectoryOnly).Select(path => $"{Path.GetFileName(path)}:{new FileInfo(path).Length}"));
                    throw new TimeoutException($"Download did not reach a terminal state after closing the tab; state={state}; streamed={Interlocked.Read(ref streamedBytes)}/{PayloadSize}; files=[{files}]");
                }
                if (mode == "cancel")
                {
                    if (outcome.State != CoreWebView2DownloadState.Interrupted || outcome.CanResume)
                        throw new InvalidOperationException($"Canceled download remained active: {outcome}");
                }
                else
                {
                    int expectedSize = mode == "parallel" ? ParallelPayloadSize : mode == "blob" ? BlobSize : mode.StartsWith("beforeunload", StringComparison.Ordinal) ? PayloadSize * 4 : mode == "dialog" ? PayloadSize * 2 : PayloadSize;
                    if (outcome.State != CoreWebView2DownloadState.Completed || !File.Exists(resultPath) || new FileInfo(resultPath).Length != expectedSize)
                        throw new InvalidOperationException($"Download after closing source tab: {outcome}");
                    using var file = File.OpenRead(resultPath);
                    var buffer = new byte[16384]; int read; long fileOffset = 0;
                    while ((read = file.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (mode == "parallel")
                        {
                            for (int i = 0; i < read; i++)
                                if (buffer[i] != (byte)((fileOffset + i) % 251)) throw new InvalidOperationException("Downloaded parallel fixture content is incorrect");
                        }
                        else if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0x5a)) throw new InvalidOperationException("Downloaded fixture content is incorrect");
                        fileOffset += read;
                    }
                }
                if (mode == "parallel")
                {
                    Console.WriteLine($"Parallel download: configured={parallelCount}, rangeSupport={!noRange}, rangeRequests={parallelOffsets.Count}, distinctNonzeroOffsets={parallelOffsets.Where(offset => offset > 0).Distinct().Count()}, unauthorized={unauthorizedRequests}");
                    int expectedRanges = noRange ? 0 : parallelCount - 1;
                    if (parallelOffsets.Where(offset => offset > 0).Distinct().Count() != expectedRanges || unauthorizedRequests != 0)
                        throw new InvalidOperationException("WebView2 did not issue the expected authenticated range requests");
                }
                if (mode == "multiple")
                {
                    if (view.IsDisposed || secondDownload!.State != CoreWebView2DownloadState.InProgress)
                        throw new InvalidOperationException("The shared download controller was released before both downloads completed");
                    if (pendingCount() != 1) throw new InvalidOperationException("Completed download was not removed from the pending count");
                    await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    string secondPath = Path.Combine(directory, "fixture2.txt");
                    if (!File.Exists(secondPath) || new FileInfo(secondPath).Length != PayloadSize * 2 || File.ReadAllBytes(secondPath).Any(value => value != 0x5a))
                        throw new InvalidOperationException("Second download is missing or incomplete");
                }
                if (!keepTab)
                {
                    await WaitUntil(() => view.IsDisposed, "download controller released after completion");
                    if (pendingCount() != 0) throw new InvalidOperationException("Finished downloads still counted as pending");
                    var retained = type.GetField("retainedDownloadTabs", PrivateInstance)!.GetValue(browser)!;
                    if ((int)retained.GetType().GetProperty("Count")!.GetValue(retained)! != 0) throw new InvalidOperationException("Closed download tab was not released");
                }
                Console.WriteLine($"PASS real WebView2 download mode={mode}, keepTab={keepTab}; outcome={outcome}; beforeunloadDialogs={beforeUnloadDialogs}");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
            finally { context.ExitThread(); }
        };
        start.Start();
        try { Application.Run(context); }
        finally { stop.Cancel(); server.Stop(); }
        return status;
    }

    private static async Task WaitUntil(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(label);
            await Task.Delay(40);
        }
    }
}
