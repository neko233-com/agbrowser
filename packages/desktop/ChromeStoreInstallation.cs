using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace AgBrowser.Desktop;

internal sealed record ChromeStoreInstallResult(bool Installed, string? Name);
internal sealed class ChromeStoreInstallException(string message, Exception inner) : IOException(message, inner);

internal static class ChromeStoreInstallation
{
    public static async Task<ChromeStoreInstallResult> InstallVerifiedAsync(
        ChromeStorePackage package, string dataDirectory, CoreWebView2Profile profile,
        Func<ChromeStorePackage, bool, bool> confirm, Action<string> progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var existing = await profile.GetBrowserExtensionsAsync();
        cancellation.ThrowIfCancellationRequested();
        bool replacing = existing.Any(item => item.Id == package.Id);
        if (!confirm(package, replacing)) return new(false, null);
        cancellation.ThrowIfCancellationRequested();
        progress("正在准备插件文件…");
        string extracted = await Task.Run(() => package.ExtractToCancellable(dataDirectory, cancellation), cancellation);
        progress("正在注册插件…");
        if (cancellation.IsCancellationRequested)
        {
            DiscardUninstalled(dataDirectory, package.Id, extracted);
            cancellation.ThrowIfCancellationRequested();
        }
        try
        {
            var installed = await profile.AddBrowserExtensionAsync(extracted);
            if (!installed.Id.Equals(package.Id, StringComparison.Ordinal))
            {
                if (!existing.Any(item => item.Id == installed.Id)) await installed.RemoveAsync();
                throw new InvalidDataException("安装后的插件 ID 与商店签名不一致。");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new ChromeStoreInstallException("插件安装失败；解包目录已保留，请先检查插件列表再重试。", ex); }
        return new(true, package.Name);
    }

    private static void DiscardUninstalled(string dataDirectory, string id, string extracted)
    {
        string parent = Path.GetFullPath(Path.Combine(dataDirectory, "store-extensions", id));
        string target = Path.GetFullPath(extracted);
        if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(target), "N", out _)) return;
        try { Directory.Delete(target, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
