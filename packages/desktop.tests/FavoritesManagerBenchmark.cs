using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AgBrowser.Desktop;

internal static class FavoritesManagerBenchmark
{
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr deviceMode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    public static void Run(string directory)
    {
        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() => {
            try
            {
                var desktop = CreateDesktop("agbrowser-favorites-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
                RunOnDesktop(directory);
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!done.Wait(TimeSpan.FromSeconds(45))) throw new TimeoutException("Favorite manager fixture timed out");
        if (failure is not null) throw new InvalidOperationException("Favorite manager fixture failed", failure);
    }

    private static void RunOnDesktop(string directory)
    {
        const int count = 30000, expectedMatches = 100;
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", Path.Combine(directory, "no-edge"));
        Program.InitializeTestApplication();
        var nodes = Enumerable.Range(0, count).Select(i => new FavoriteNode(i.ToString(), $"Bookmark {i:D5}", $"https://example.invalid/{i}", [])).ToArray();
        var mobile = new FavoriteNode("mobile", "移动设备收藏", null, [new FavoriteNode("extra", "Extra root bookmark", "https://extra-root.example.test/resource", [])]);
        using var browser = new BrowserWindow([]);
        _ = browser.Handle;
        var type = typeof(BrowserWindow);
        ((System.Windows.Forms.Timer)type.GetField("favoritesTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(browser)!).Stop();
        type.GetField("displayedFavorites", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(browser, new FavoriteSnapshot("Default", nodes, null, "1", AdditionalRoots: [mobile]));
        using (var icon = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(icon))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.Red); icon.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            type.GetField("favoriteIcons", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(browser, new Dictionary<string, byte[]> { ["https://example.invalid/29900"] = stream.ToArray() });
        }
        var clock = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 20 };
        int phase = 0; Exception? callbackError = null;
        timer.Tick += (_, _) => {
            var dialog = Application.OpenForms.OfType<ChromeDialog>().FirstOrDefault(item => item.Text == "收藏管理");
            if (dialog is null) return;
            try
            {
                var search = Descendants(dialog).OfType<TextBox>().Single(item => item.AccessibleName == "搜索收藏");
                var list = Descendants(dialog).Single(item => item.AccessibleName == "收藏搜索结果");
                int resultCount = list is ListView view ? view.VirtualListSize : ((ListBox)list).Items.Count;
                bool complete = Descendants(dialog).OfType<Label>().Any(item => item.Text.StartsWith($"{resultCount} / ", StringComparison.Ordinal));
                if (phase == 0)
                {
                    if (resultCount != count + 1) return;
                    long opening = clock.ElapsedMilliseconds;
                    search.Text = "Bookmark 1";
                    search.Text = "Bookmark 299";
                    clock.Restart(); phase = 1;
                    Console.WriteLine($"Favorite manager first results: {count + 1} entries in {opening} ms");
                }
                else if (phase == 1 && resultCount == expectedMatches)
                {
                    Console.WriteLine($"Favorite manager filtered results: {expectedMatches} entries in {clock.ElapsedMilliseconds} ms (includes debounce)");
                    if (Environment.GetEnvironmentVariable("AGB_TEST_RENDER_OUTPUT") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        using var image = new Bitmap(dialog.Width, dialog.Height);
                        dialog.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(output, "favorites-manager.png"));
                    }
                    if (list is ListView virtualList)
                    {
                        virtualList.Focus(); virtualList.Items[0].Selected = true;
                        if (virtualList.SelectedIndices.Count != 1 || virtualList.SelectedIndices[0] != 0) throw new InvalidOperationException("Virtual favorite selection is incorrect");
                        using var listImage = new Bitmap(virtualList.Width, virtualList.Height);
                        virtualList.DrawToBitmap(listImage, new Rectangle(Point.Empty, listImage.Size));
                        var first = virtualList.GetItemRect(0); var second = virtualList.GetItemRect(1);
                        var selectedColor = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(232, 240, 254);
                        var ordinaryColor = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
                        if (listImage.GetPixel(2, first.Top + 3).ToArgb() != selectedColor.ToArgb() || listImage.GetPixel(2, second.Top + 3).ToArgb() != ordinaryColor.ToArgb())
                            throw new InvalidOperationException("Only the selected favorite should have a selected background");
                        var iconPixel = listImage.GetPixel(16, first.Top + first.Height / 2);
                        if (iconPixel.R < 200 || iconPixel.G > 60 || iconPixel.B > 60) throw new InvalidOperationException("Cached favorite icon was not drawn");
                    }
                    else ((ListBox)list).SelectedIndex = 0;
                    var edit = Descendants(dialog).OfType<Button>().Single(item => item.Text == "编辑标签");
                    if (!edit.Enabled) throw new InvalidOperationException("Selected favorite cannot be edited");
                    phase = 2; edit.PerformClick();
                }
                else if (phase == 2)
                {
                    var tagsDialog = Application.OpenForms.OfType<ChromeDialog>().FirstOrDefault(item => item.Text == "收藏标签");
                    if (tagsDialog is null) return;
                    if (tagsDialog.Owner != dialog) throw new InvalidOperationException("Tag dialog should belong to the favorites manager");
                    Descendants(tagsDialog).OfType<TextBox>().Single(item => item.AccessibleName == "收藏标签，以逗号分隔").Text = "性能测试";
                    phase = 3; ((Button)tagsDialog.AcceptButton!).PerformClick();
                }
                else if (phase == 3 && resultCount == expectedMatches && complete && !Application.OpenForms.OfType<ChromeDialog>().Any(item => item.Text == "收藏标签"))
                {
                    search.Text = "性能测试"; phase = 4;
                }
                else if (phase == 4 && resultCount == 1 && complete)
                {
                    if (!new FavoriteTags(directory).Get("https://example.invalid/29900").Contains("性能测试")) throw new InvalidOperationException("Edited tag was not saved");
                    search.Text = "example.invalid/12345"; phase = 5;
                }
                else if (phase == 5 && resultCount == 1 && complete)
                {
                    string text = list is ListView virtualList ? virtualList.Items[0].Text : ((ListBox)list).Items[0]!.ToString()!;
                    if (!text.Contains("example.invalid/12345")) throw new InvalidOperationException("URL search returned the wrong favorite");
                    search.Text = "extra-root.example.test"; phase = 6;
                }
                else if (phase == 6 && resultCount == 1 && complete)
                {
                    string text = list is ListView virtualList ? virtualList.Items[0].Text : ((ListBox)list).Items[0]!.ToString()!;
                    if (!text.Contains("Extra root bookmark")) throw new InvalidOperationException("Additional-root search returned the wrong favorite");
                    search.Text = "no matching favorite"; phase = 7;
                }
                else if (phase == 7 && resultCount == 0 && complete)
                {
                    search.Text = "example.invalid/12345"; phase = 8;
                }
                else if (phase == 8 && resultCount == 1 && complete)
                {
                    if (list is ListView virtualList) { virtualList.Focus(); virtualList.Items[0].Selected = true; }
                    else ((ListBox)list).SelectedIndex = 0;
                    int selectionCount = list is ListView selectedView ? selectedView.SelectedIndices.Count : ((ListBox)list).SelectedIndex >= 0 ? 1 : 0;
                    bool openEnabled = ((Button)dialog.AcceptButton!).Enabled;
                    int openClicks = 0; ((Button)dialog.AcceptButton!).Click += (_, _) => openClicks++;
                    phase = 9; timer.Stop();
                    bool handled = (bool)typeof(ChromeDialog).GetMethod("ProcessDialogKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [Keys.Enter])!;
                    if (!handled || selectionCount != 1 || !openEnabled || openClicks != 1) throw new InvalidOperationException("Enter did not activate the selected favorite");
                    Console.WriteLine("PASS favorite selection, cached icon, modal ownership, saved tags, search, and Enter");
                }
            }
            catch (Exception ex) { callbackError = ex; timer.Stop(); dialog.Close(); }
        };
        timer.Start();
        type.GetMethod("ShowFavoritesManager", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(browser, []);
        if (callbackError is not null) throw callbackError;
        if (phase != 9) throw new InvalidOperationException("Favorite manager was not fully exercised");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
