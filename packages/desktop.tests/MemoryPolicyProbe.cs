using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AgBrowser.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class MemoryPolicyProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(directory, "no-edge"));
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--no-proxy-server");
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () => {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var request = await server.GetContextAsync();
                    string allocation = request.Request.QueryString["light"] == "1" ? "0" : "32 * 1024 * 1024";
                    byte[] body = Encoding.UTF8.GetBytes($"<!doctype html><title>Memory fixture</title><script>window.fixtureData = new Uint8Array({allocation}).fill(90); window.fixtureTicks = 0; setInterval(() => window.fixtureTicks++, 250);</script>");
                    request.Response.ContentType = "text/html"; request.Response.ContentLength64 = body.Length;
                    await request.Response.OutputStream.WriteAsync(body, stop.Token); request.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { break; }
            }
        });
        Program.InitializeTestApplication();
        using var browser = new BrowserWindow(Enumerable.Range(0, 3).Select(i => $"http://127.0.0.1:{port}/?tab={i}").ToArray());
        _ = browser.Handle;
        using var context = new ApplicationContext();
        using var start = new System.Windows.Forms.Timer { Interval = 10 };
        int status = 0;
        start.Tick += async (_, _) => {
            start.Stop();
            try
            {
                Type type = typeof(BrowserWindow);
                await ((Task)type.GetMethod("StartBrowser", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(30));
                var tabs = (IList)type.GetField("tabs", PrivateInstance)!.GetValue(browser)!;
                if (tabs.Count != 3) throw new InvalidOperationException("Expected three fixture pages");
                var views = tabs.Cast<object>().Select(tab => (WebView2)tab.GetType().GetProperty("View")!.GetValue(tab)!).ToArray();
                foreach (var view in views) await WaitUntil(() => view.CoreWebView2?.DocumentTitle == "Memory fixture", "page ready");
                await WaitUntil(() => tabs.Cast<object>().All(tab => !(bool)tab.GetType().GetProperty("IsNavigating")!.GetValue(tab)!), "all page navigations complete");
                await Task.Delay(1500);
                var environment = views[0].CoreWebView2.Environment;
                var before = Measure(environment);
                var apply = type.GetMethod("ApplyBackgroundMemoryPolicy", PrivateInstance)!;
                apply.Invoke(browser, []);
                if (views.Any(view => view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal))
                    throw new InvalidOperationException("A recently used page entered the low memory target too early");
                long inactive = Stopwatch.GetTimestamp() - (long)(TimeSpan.FromMinutes(3).TotalSeconds * Stopwatch.Frequency);
                foreach (object tab in tabs) tab.GetType().GetProperty("InactiveSince")!.SetValue(tab, inactive);
                var timer = (System.Windows.Forms.Timer)type.GetField("backgroundMemoryTimer", PrivateInstance)!.GetValue(browser)!;
                timer.Interval = 50; timer.Start();
                await WaitUntil(() => views.Take(2).All(view => view.CoreWebView2.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low), "background memory timer");
                timer.Stop();
                if (views.Take(2).Any(view => view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Low))
                    throw new InvalidOperationException("Low memory target was not accepted");
                if (views[2].CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                    throw new InvalidOperationException("The active page entered the low memory target");
                var tickBefore = int.Parse(await views[0].CoreWebView2.ExecuteScriptAsync("window.fixtureTicks"));
                await Task.Delay(8000);
                var tickAfter = int.Parse(await views[0].CoreWebView2.ExecuteScriptAsync("window.fixtureTicks"));
                if (tickAfter <= tickBefore) throw new InvalidOperationException("Background script stopped after requesting the low memory target");
                var after = Measure(environment);
                var resumeWatch = Stopwatch.StartNew();
                type.GetMethod("SelectTab", PrivateInstance)!.Invoke(browser, [tabs[0]!]);
                if (views[0].CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                    throw new InvalidOperationException("Selecting a page did not restore its normal memory target");
                if (await views[0].CoreWebView2.ExecuteScriptAsync("window.fixtureData.length") != (32 * 1024 * 1024).ToString())
                    throw new InvalidOperationException("Background page data was not retained");
                resumeWatch.Stop();
                Console.WriteLine($"PASS isolated WebView2 memory-target probe: processes={after.Count}, privateMiB={before.PrivateMiB}->{after.PrivateMiB}, workingSetMiB={before.WorkingSetMiB}->{after.WorkingSetMiB}, ticks={tickBefore}->{tickAfter}, normalTargetAndScriptMs={resumeWatch.ElapsedMilliseconds}");
                browser.WindowState = FormWindowState.Minimized;
                typeof(Control).GetMethod("OnResize", PrivateInstance)!.Invoke(browser, [EventArgs.Empty]);
                tabs[0]!.GetType().GetProperty("InactiveSince")!.SetValue(tabs[0], inactive);
                apply.Invoke(browser, []);
                if (views[0].CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Low)
                    throw new InvalidOperationException("An idle minimized page did not enter low memory mode");
                browser.WindowState = FormWindowState.Normal;
                typeof(Control).GetMethod("OnResize", PrivateInstance)!.Invoke(browser, [EventArgs.Empty]);
                if (views[0].CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                    throw new InvalidOperationException("Restoring the window did not restore the active page's normal memory target");
                Console.WriteLine("PASS minimized active page reduces its memory target and restores it with the window");
                views[0].CoreWebView2.IsMuted = true;
                await views[0].CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                await views[0].CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                await views[0].CoreWebView2.ExecuteScriptAsync("window.fixtureAudio = new AudioContext(); window.fixtureOsc = fixtureAudio.createOscillator(); fixtureOsc.connect(fixtureAudio.destination); fixtureOsc.start(); fixtureAudio.resume();");
                try
                {
                    await WaitUntil(() => views[0].CoreWebView2.IsDocumentPlayingAudio, "muted fixture audio starts", TimeSpan.FromSeconds(5));
                    type.GetMethod("SelectTab", PrivateInstance)!.Invoke(browser, [tabs[2]!]);
                    tabs[0]!.GetType().GetProperty("InactiveSince")!.SetValue(tabs[0], inactive);
                    apply.Invoke(browser, []);
                    if (views[0].CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Normal)
                        throw new InvalidOperationException("An audio-playing page entered low memory mode");
                    Console.WriteLine("PASS playing audio is excluded from the background memory target (fixture audio was muted)");
                }
                catch (TimeoutException) { Console.WriteLine("SKIP audio exclusion: muted fixture audio did not start on this machine"); }
                finally { await views[0].CoreWebView2.ExecuteScriptAsync("window.fixtureOsc?.stop(); window.fixtureAudio?.close();"); }
                var addTab = type.GetMethod("AddTab", PrivateInstance)!;
                int originalCount = tabs.Count;
                for (int i = 0; i < 10; i++)
                    await ((Task)addTab.Invoke(browser, [$"http://127.0.0.1:{port}/?light=1&tab={i}", true, false, false])!).WaitAsync(TimeSpan.FromSeconds(10));
                var extraTabs = tabs.Cast<object>().Skip(originalCount).ToArray();
                var extraViews = extraTabs.Select(tab => (WebView2)tab.GetType().GetProperty("View")!.GetValue(tab)!).ToArray();
                foreach (var view in extraViews) await WaitUntil(() => view.CoreWebView2?.DocumentTitle == "Memory fixture", "batch page ready");
                await WaitUntil(() => extraTabs.All(tab => !(bool)tab.GetType().GetProperty("IsNavigating")!.GetValue(tab)!), "batch page navigations complete");
                long now = Stopwatch.GetTimestamp();
                foreach (object tab in tabs) tab.GetType().GetProperty("InactiveSince")!.SetValue(tab, now);
                foreach (object tab in extraTabs.Take(9)) tab.GetType().GetProperty("InactiveSince")!.SetValue(tab, inactive);
                apply.Invoke(browser, []);
                int firstBatch = extraViews.Take(9).Count(view => view.CoreWebView2.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low);
                if (firstBatch != 8) throw new InvalidOperationException($"Expected eight pages in the first memory batch, received {firstBatch}");
                apply.Invoke(browser, []);
                if (extraViews.Take(9).Any(view => view.CoreWebView2.MemoryUsageTargetLevel != CoreWebView2MemoryUsageTargetLevel.Low))
                    throw new InvalidOperationException("Later background pages were starved by the batch limit");
                Console.WriteLine("PASS memory policy limits a batch to eight pages and advances to the remaining page");
                Console.WriteLine("Scope: three local pages, hidden window, one sample; memory targets are best effort and this is not a real-site or cold-start benchmark.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
            finally { context.ExitThread(); }
        };
        start.Start();
        try { Application.Run(context); }
        finally { stop.Cancel(); server.Stop(); }
        return status;
    }

    private static (int Count, long PrivateMiB, long WorkingSetMiB) Measure(CoreWebView2Environment environment)
    {
        long privateBytes = 0, workingSet = 0; int count = 0;
        foreach (var info in environment.GetProcessInfos())
        {
            try { using var process = Process.GetProcessById((int)info.ProcessId); privateBytes += process.PrivateMemorySize64; workingSet += process.WorkingSet64; count++; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return (count, privateBytes / 1048576, workingSet / 1048576);
    }

    private static async Task WaitUntil(Func<bool> predicate, string label, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(label); await Task.Delay(40); }
    }
}
