using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AgBrowser.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class ClosePageProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Run(string directory)
    {
        using var watchdog = new System.Threading.Timer(_ => { Console.Error.WriteLine("Probe watchdog expired"); Environment.Exit(2); }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
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
                HttpListenerContext request;
                try { request = await server.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Close fixture</title><button>Activate</button>");
                    request.Response.ContentType = "text/html; charset=utf-8"; request.Response.ContentLength64 = body.Length;
                    await request.Response.OutputStream.WriteAsync(body, stop.Token); request.Response.Close();
                }
                catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
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
                var type = typeof(BrowserWindow);
                Console.WriteLine("Starting isolated browser");
                await ((Task)type.GetMethod("StartBrowser", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine("Browser initialized");
                var tabs = (IList)type.GetField("tabs", PrivateInstance)!.GetValue(browser)!;
                var tab = tabs[0]!;
                var view = (WebView2)tab.GetType().GetProperty("View")!.GetValue(tab)!;
                await WaitUntil(() => view.CoreWebView2?.DocumentTitle == "Close fixture", "local page");
                Console.WriteLine("Fixture page loaded");
                var core = view.CoreWebView2;
                if (Environment.GetEnvironmentVariable("AGB_CLOSE_PROBE_NATIVE") != "1") core.Settings.AreDefaultScriptDialogsEnabled = false;
                int dialogCount = 0;
                bool accept = false;
                core.GetDevToolsProtocolEventReceiver("Page.javascriptDialogOpening").DevToolsProtocolEventReceived += (_, e) => {
                    using var json = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                    if (json.RootElement.GetProperty("type").GetString() != "beforeunload") return;
                    dialogCount++;
                    browser.BeginInvoke(async () => await core.CallDevToolsProtocolMethodAsync("Page.handleJavaScriptDialog", accept ? "{\"accept\":true}" : "{\"accept\":false}"));
                };
                bool withoutUnload = Environment.GetEnvironmentVariable("AGB_CLOSE_PROBE_NO_UNLOAD") == "1";
                if (!withoutUnload) await core.ExecuteScriptAsync("window.onbeforeunload = e => { e.preventDefault(); e.returnValue = 'unsaved'; return 'unsaved'; }");
                await core.ExecuteScriptAsync("window.__agbLife = []; for (const name of ['pagehide', 'unload']) addEventListener(name, () => window.__agbLife.push(name));");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\",\"x\":5,\"y\":5,\"button\":\"left\",\"clickCount\":1}");
                if (await core.ExecuteScriptAsync("navigator.userActivation.hasBeenActive") != "true") throw new InvalidOperationException("Fixture did not receive trusted activation");
                if (Environment.GetEnvironmentVariable("AGB_CLOSE_PROBE_RAW") == "1")
                {
                    int closeEvents = 0;
                    core.WindowCloseRequested += (_, _) => closeEvents++;
                    await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                    Console.WriteLine("Calling Page.close with cancel and CDP dialog handling");
                    await core.CallDevToolsProtocolMethodAsync("Page.close", "{}").WaitAsync(TimeSpan.FromSeconds(10));
                    if (withoutUnload)
                    {
                        await WaitUntil(() => closeEvents == 1, "raw close event without beforeunload");
                        Console.WriteLine($"raw no handler: dialogs={dialogCount}, closeEvents={closeEvents}, title={await core.ExecuteScriptAsync("document.title")}");
                        return;
                    }
                    await WaitUntil(() => dialogCount == 1, "raw close cancel dialog");
                    Console.WriteLine($"raw cancel: dialogs={dialogCount}, closeEvents={closeEvents}, title={await core.ExecuteScriptAsync("document.title")}");
                    accept = true;
                    Console.WriteLine("Calling Page.close with accept");
                    await core.CallDevToolsProtocolMethodAsync("Page.close", "{}").WaitAsync(TimeSpan.FromSeconds(10));
                    await Task.Delay(300);
                    Console.WriteLine($"raw accept: dialogs={dialogCount}, closeEvents={closeEvents}, disposed={view.IsDisposed}, tabs={tabs.Count}, title={await core.ExecuteScriptAsync("document.title").WaitAsync(TimeSpan.FromSeconds(5))}, lifecycle={await core.ExecuteScriptAsync("JSON.stringify(window.__agbLife)")}");
                    return;
                }
                var requestClose = type.GetMethod("RequestCloseTab", PrivateInstance)!;
                string originalUrl = (string)tab.GetType().GetProperty("Url")!.GetValue(tab)!;
                object? previous = null;
                if (Environment.GetEnvironmentVariable("AGB_CLOSE_PROBE_BACKGROUND") == "1")
                {
                    var added = (Task)type.GetMethod("AddTab", PrivateInstance)!.Invoke(browser, ["about:blank", true, false, false])!;
                    await added.WaitAsync(TimeSpan.FromSeconds(10));
                    previous = added.GetType().GetProperty("Result")!.GetValue(added);
                }
                if (!withoutUnload)
                {
                    var firstClose = (Task<bool>)requestClose.Invoke(browser, [tab])!;
                    if (await ((Task<bool>)requestClose.Invoke(browser, [tab])!).WaitAsync(TimeSpan.FromSeconds(2)))
                        throw new InvalidOperationException("Duplicate close request was not rejected");
                    bool canceled = await firstClose.WaitAsync(TimeSpan.FromSeconds(10));
                    if (canceled || dialogCount != 1 || !tabs.Contains(tab) || view.IsDisposed || core.Source != originalUrl || await core.ExecuteScriptAsync("document.title") != "\"Close fixture\"")
                        throw new InvalidOperationException("Canceling beforeunload did not preserve the original tab and page");
                    if ((string)tab.GetType().GetProperty("Url")!.GetValue(tab)! != originalUrl)
                        throw new InvalidOperationException("Canceling beforeunload changed the saved tab URL");
                }
                accept = true;
                bool closed;
                if (Environment.GetEnvironmentVariable("AGB_CLOSE_PROBE_SHORTCUT") == "1")
                {
                    var key = new KeyEventArgs(Keys.Control | Keys.F4);
                    type.GetMethod("OnShortcut", PrivateInstance)!.Invoke(browser, [view, key]);
                    if (!key.Handled) throw new InvalidOperationException("Ctrl+F4 was not handled");
                    await WaitUntil(() => !tabs.Contains(tab), "Ctrl+F4 closes the current tab");
                    closed = true;
                }
                else closed = await ((Task<bool>)requestClose.Invoke(browser, [tab])!).WaitAsync(TimeSpan.FromSeconds(10));
                if (!closed || tabs.Contains(tab) || !view.IsDisposed || dialogCount != (withoutUnload ? 0 : 2))
                    throw new InvalidOperationException("Closing after confirmation did not remove the original tab");
                var closedTabs = (IList)type.GetField("recentlyClosed", PrivateInstance)!.GetValue(browser)!;
                object saved = closedTabs[closedTabs.Count - 1]!;
                if ((string)saved.GetType().GetProperty("Url")!.GetValue(saved)! != originalUrl || (string?)saved.GetType().GetProperty("Title")!.GetValue(saved) != "Close fixture")
                    throw new InvalidOperationException("Recently closed entry lost the original page URL or title");
                if (withoutUnload && previous is not null && !ReferenceEquals(type.GetField("active", PrivateInstance)!.GetValue(browser), previous))
                    throw new InvalidOperationException("Closing a background tab changed the selected tab");
                await ((Task)type.GetMethod("ReopenClosedTab", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(10));
                await WaitUntil(() => tabs.Cast<object>().Any(item => (string)item.GetType().GetProperty("Url")!.GetValue(item)! == originalUrl), "reopened tab URL");
                Console.WriteLine(withoutUnload ? "PASS normal tab closes without a prompt" : "PASS beforeunload cancel preserves the page and accept closes it");
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
