using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AgBrowser.Desktop;

public sealed partial class BrowserWindow
{
    private readonly SemaphoreSlim closeGate = new(1, 1);

    private async Task<bool> RequestCloseTab(BrowserTab tab)
    {
        if (tab.ClosePending || !tabs.Contains(tab)) return false;
        tab.ClosePending = true;
        await closeGate.WaitAsync();
        try
        {
            if (IsDisposed || Disposing || recoveryInProgress || !tabs.Contains(tab)) return false;
            if (browserProcessFailed || tab.Failed || tab.IsNewTab || tab.View.CoreWebView2 is not { } core)
            {
                tab.ClosePending = false;
                FinishCloseTab(tab);
                return true;
            }

            var previous = active;
            if (active != tab) SelectTab(tab); // The native prompt belongs to the page being closed.
            var snapshot = new ClosedTab(tab.Url, tab.Title, SameIconPage(tab.IconSource, tab.Url) ? tab.IconData : null, tabs.IndexOf(tab));
            var view = tab.View;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool beforeUnloadOpen = false;
            bool pageDomainEnabled = false;
            var opening = core.GetDevToolsProtocolEventReceiver("Page.javascriptDialogOpening");
            var closed = core.GetDevToolsProtocolEventReceiver("Page.javascriptDialogClosed");
            void OnOpening(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
            {
                try
                {
                    using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
                    beforeUnloadOpen = json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "beforeunload";
                }
                catch (JsonException) { completion.TrySetResult(false); }
            }
            void OnClosed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
            {
                if (!beforeUnloadOpen) return;
                beforeUnloadOpen = false;
                try
                {
                    using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
                    if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.True)
                        completion.TrySetResult(false);
                }
                catch (JsonException) { completion.TrySetResult(false); }
            }
            void OnWindowClose(object? sender, object e) => completion.TrySetResult(true);
            void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) => completion.TrySetResult(false);

            opening.DevToolsProtocolEventReceived += OnOpening;
            closed.DevToolsProtocolEventReceived += OnClosed;
            core.WindowCloseRequested += OnWindowClose;
            core.ProcessFailed += OnProcessFailed;
            bool canClose = false;
            try
            {
                // Observe the native dialog's cancellation and the host's
                // close request without replacing or accepting the dialog.
                await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").WaitAsync(TimeSpan.FromSeconds(5));
                pageDomainEnabled = true;
                if (!tabs.Contains(tab) || IsDisposed || Disposing || !ReferenceEquals(tab.View, view)) return false;
                await core.CallDevToolsProtocolMethodAsync("Page.close", "{}");
                canClose = await completion.Task;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                if (!IsDisposed && !Disposing && Visible && tabs.Contains(tab))
                    ChromeMessage.Show(this, "无法确认页面是否有未保存的内容，标签页已保留。", "agbrowser");
            }
            finally
            {
                opening.DevToolsProtocolEventReceived -= OnOpening;
                closed.DevToolsProtocolEventReceived -= OnClosed;
                core.WindowCloseRequested -= OnWindowClose;
                core.ProcessFailed -= OnProcessFailed;
                if (pageDomainEnabled && !view.IsDisposed)
                    try { await core.CallDevToolsProtocolMethodAsync("Page.disable", "{}").WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
            }
            if (!canClose || IsDisposed || Disposing || !tabs.Contains(tab) || !ReferenceEquals(tab.View, view)) return false;
            var restore = active == tab && previous != tab && previous is not null && tabs.Contains(previous) ? previous : null;
            tab.ClosePending = false;
            FinishCloseTab(tab, preferredTab: restore, snapshot: snapshot);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or NotImplementedException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (!IsDisposed && !Disposing && Visible && tabs.Contains(tab))
                ChromeMessage.Show(this, "无法确认页面是否有未保存的内容，标签页已保留。", "agbrowser");
            return false;
        }
        finally
        {
            tab.ClosePending = false;
            closeGate.Release();
        }
    }
}
