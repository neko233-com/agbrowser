using System.Text.Json;

namespace AgBrowser.Desktop;

/// <summary>Persists explicit per-site exceptions without changing browser security settings.</summary>
internal sealed class AdBlockSettings
{
    private readonly string path;
    private HashSet<string> exceptions = new(StringComparer.OrdinalIgnoreCase);
    private bool readable = true;
    public AdBlockSettings(string dataDir)
    {
        path = Path.Combine(dataDir, "adblock-exceptions.json");
        try
        {
            if (File.Exists(path)) exceptions = new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { readable = false; }
    }
    public bool EnabledFor(string? pageUrl)
    {
        return !Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || !exceptions.Contains(uri.Host);
    }
    internal static bool IsGoogleChallenge(string? pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        string host = uri.IdnHost;
        return (host.Equals("google.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)) &&
               (uri.AbsolutePath.Equals("/sorry", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/sorry/", StringComparison.OrdinalIgnoreCase));
    }
    public void SetEnabled(string host, bool enabled)
    {
        if (!readable) throw new IOException("现有广告拦截设置无法读取，未覆盖原文件。");
        var updated = new HashSet<string>(exceptions, StringComparer.OrdinalIgnoreCase);
        if (enabled) updated.Remove(host); else updated.Add(host);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        File.Move(temporary, path, true);
        exceptions = updated;
    }
}
