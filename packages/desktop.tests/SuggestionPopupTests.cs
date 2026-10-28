using System.ComponentModel;
using System.Runtime.InteropServices;
using AgBrowser.Desktop;

internal static class SuggestionPopupTests
{
    private const int ExtendedStyle = -20;
    private const int TopmostStyle = 0x00000008;

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr deviceMode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    public static void Run()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() => { try { RunOnIsolatedDesktop(); } catch (Exception ex) { failure = ex; } finally { completed.Set(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Isolated suggestion window test did not complete.");
        if (failure is not null) throw new InvalidOperationException("Isolated suggestion window test failed.", failure);
    }

    private static void RunOnIsolatedDesktop()
    {
        // A separate, invisible Windows desktop avoids taking focus from the user.
        IntPtr desktop = CreateDesktop("agbrowser-suggestion-test-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Program.InitializeTestApplication();
        var owner = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = Point.Empty, Size = new Size(500, 300) };
        owner.Show();
        var baseline = new ToolStripDropDown { AutoClose = false };
        baseline.Items.Add(new ToolStripLabel("baseline"));
        baseline.Show(owner, new Point(10, 30));
        Assert(baseline.Visible && IsTopmost(baseline), "WinForms default dropdown reproduces the topmost style");
        baseline.Close();

        Type popupType = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.AddressSuggestionPopup")!;
        var popup = (ToolStripDropDown)Activator.CreateInstance(popupType, nonPublic: true)!;
        popup.AutoClose = false;
        var showAttached = popupType.GetMethod("ShowAttached")!;
        var list = new ListBox { Items = { "suggestion" } };
        popup.Items.Add(new ToolStripControlHost(list));
        for (int i = 0; i < 2; i++)
        {
            showAttached.Invoke(popup, [owner, new Point(10, 30)]);
            Assert(popup.Visible, "address suggestions remain visible when opened");
            Assert(!IsTopmost(popup), "address suggestions do not use the topmost window style");
            Assert(IsAbove(popup.Handle, owner.Handle), "suggestions remain above their browser window");
            popup.Location = new Point(popup.Left + 5, popup.Top + 5);
            Assert(!IsTopmost(popup), "moving the dropdown does not restore topmost");
            popup.Close();
            Assert(!popup.Visible, "address suggestions close normally");
        }
        owner.Close();
        Console.WriteLine("PASS isolated Windows desktop: default popup is topmost; address suggestions are not topmost after opening, moving or reopening");
    }

    private static bool IsTopmost(Control control) => (GetWindowLong(control.Handle, ExtendedStyle) & TopmostStyle) != 0;
    private static bool IsAbove(IntPtr first, IntPtr second)
    {
        for (IntPtr window = first; window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == second) return true;
        return false;
    }
    private static void Assert(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
}
