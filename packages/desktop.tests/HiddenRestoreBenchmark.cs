using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgBrowser.Desktop;

internal static class HiddenRestoreBenchmark
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static int Run(string directory)
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("AGB_RESTORE_BENCH_TABS"), out int value) ? Math.Clamp(value, 1, 2000) : 400;
        int selected = int.TryParse(Environment.GetEnvironmentVariable("AGB_RESTORE_BENCH_SELECTED"), out int selectedValue) ? Math.Clamp(selectedValue, 0, count - 1) : count - 1;
        bool external = Environment.GetEnvironmentVariable("AGB_RESTORE_BENCH_EXTERNAL") == "1";
        Directory.CreateDirectory(directory);
        string[] urls = Enumerable.Range(0, count).Select(i => i == selected ? "about:blank" : $"https://example.invalid/tab-{i}").ToArray();
        string[] titles = Enumerable.Range(0, count).Select(i => $"Fixture {i}").ToArray();
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new { version = 1, urls, titles, activeIndex = selected }));
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(directory, "no-edge"));
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--no-proxy-server");
        Program.InitializeTestApplication();

        string[] expectedUrls = external ? [.. urls, "about:blank"] : urls;
        int expectedSelected = external ? count : selected;
        using var window = new BrowserWindow(external ? ["about:blank"] : []);
        _ = window.Handle; // This benchmark never shows or activates the window.
        using var context = new ApplicationContext();
        using var start = new System.Windows.Forms.Timer { Interval = 10 };
        int status = 0;
        start.Tick += async (_, _) => {
            start.Stop();
            try
            {
                Type type = typeof(BrowserWindow);
                var watch = Stopwatch.StartNew();
                await ((Task)type.GetMethod("StartBrowser", PrivateInstance)!.Invoke(window, [])!).WaitAsync(TimeSpan.FromSeconds(60));
                watch.Stop();
                var tabs = (IList)type.GetField("tabs", PrivateInstance)!.GetValue(window)!;
                var rail = (FlowLayoutPanel)type.GetField("rail", PrivateInstance)!.GetValue(window)!;
                if (tabs.Count != expectedUrls.Length || !ReferenceEquals(type.GetField("active", PrivateInstance)!.GetValue(window), tabs[expectedSelected]))
                    throw new InvalidOperationException("Restored tab count or active tab is incorrect.");
                for (int i = 0; i < expectedUrls.Length; i++)
                {
                    object tab = tabs[i]!;
                    var button = (RailTabButton)tab.GetType().GetProperty("Button")!.GetValue(tab)!;
                    if ((string)tab.GetType().GetProperty("Url")!.GetValue(tab)! != expectedUrls[i] || rail.Controls.GetChildIndex(button) != i || button.Selected != (i == expectedSelected))
                        throw new InvalidOperationException($"Restored tab order is incorrect at {i}.");
                }
                int expectedHeight = rail.Padding.Vertical + rail.AutoScrollMargin.Height + rail.Controls.Cast<Control>().Sum(control => control.Height + control.Margin.Vertical);
                if (rail.AutoScrollMinSize.Height != expectedHeight) throw new InvalidOperationException("Restored rail scroll height is incorrect.");
                Console.WriteLine($"PASS hidden restored session: {expectedUrls.Length} tabs, selected index {expectedSelected}, {watch.ElapsedMilliseconds} ms{(external ? " (external URL)" : "")}");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); status = 1; }
            finally { context.ExitThread(); }
        };
        start.Start();
        Application.Run(context);
        return status;
    }
}
