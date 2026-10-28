using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using AgBrowser.Desktop;
using Microsoft.Web.WebView2.WinForms;

internal static class FindShortcutTests
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
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () => {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext request;
                try { request = await server.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                try
                {
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><title>Find fixture</title><p>needle alpha needle</p>");
                    request.Response.ContentType = "text/html; charset=utf-8";
                    request.Response.ContentLength64 = body.Length;
                    await request.Response.OutputStream.WriteAsync(body, stop.Token);
                    request.Response.Close();
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
                await ((Task)type.GetMethod("StartBrowser", PrivateInstance)!.Invoke(browser, [])!).WaitAsync(TimeSpan.FromSeconds(30));
                var tabs = (IList)type.GetField("tabs", PrivateInstance)!.GetValue(browser)!;
                var tab = tabs[0]!;
                var view = (WebView2)tab.GetType().GetProperty("View")!.GetValue(tab)!;
                await WaitUntil(() => view.CoreWebView2?.DocumentTitle == "Find fixture", "local page");
                var canHandle = type.GetMethod("CanHandleShortcut", PrivateInstance)!;
                if (!(bool)canHandle.Invoke(browser, [Keys.Control | Keys.F])!)
                    throw new InvalidOperationException("Ctrl+F from browser controls was not routed to the page");
                var core = view.CoreWebView2;
                var options = core.Environment.CreateFindOptions();
                options.FindTerm = "needle";
                // Hidden fixture windows cannot display the native find bar;
                // exercise Chromium matching without claiming to test its UI.
                options.SuppressDefaultFindDialog = true;
                await core.Find.StartAsync(options).WaitAsync(TimeSpan.FromSeconds(15));
                await WaitUntil(() => core.Find.MatchCount == 2 && core.Find.ActiveMatchIndex == 1, "first find match");
                core.Find.FindNext();
                await WaitUntil(() => core.Find.ActiveMatchIndex == 2, "next find match");
                core.Find.FindPrevious();
                await WaitUntil(() => core.Find.ActiveMatchIndex == 1, "previous find match");
                core.Find.Stop();
                var shortcut = type.GetMethod("OnShortcut", PrivateInstance)!;
                var findKey = new KeyEventArgs(Keys.Control | Keys.F);
                shortcut.Invoke(browser, [browser, findKey]);
                shortcut.Invoke(browser, [browser, findKey]);
                if (!findKey.Handled || !findKey.SuppressKeyPress)
                    throw new InvalidOperationException("Ctrl+F from browser controls was not consumed");
                await Task.Delay(100); // Allow the queued shortcut to reach WebView2.
                await ((Task)type.GetMethod("AddTab", PrivateInstance)!.Invoke(browser, ["about:blank", true, false, false])!).WaitAsync(TimeSpan.FromSeconds(10));
                if ((bool)canHandle.Invoke(browser, [Keys.Control | Keys.F])!)
                    throw new InvalidOperationException("Blank tab should not open a find bar for an inactive page");
                Console.WriteLine("PASS hidden WebView2 find match count, next/previous and blank-tab shortcut routing (native find UI not displayed)");
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
