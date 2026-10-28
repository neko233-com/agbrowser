using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgBrowser.Desktop;

/// <summary>Reads bookmarked-page icons from temporary, read-only browser cache snapshots.</summary>
internal static class FavoriteIcons
{
    private const int SqliteOk = 0, SqliteRow = 100;
    private const int MaxIconBytes = 12 * 1024 * 1024;
    private sealed record IconHit(byte[] Data, bool Exact);
    private sealed record CacheRead(Dictionary<string, IconHit> Hits, int Bytes);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_reset(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_bytes(IntPtr stmt, int column);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr stmt);

    public static Dictionary<string, byte[]> Read(FavoriteSnapshot snapshot, CancellationToken cancellation)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string edgeData = Environment.GetEnvironmentVariable("AGB_EDGE_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
        string profile = snapshot.Profile;
        if (profile != "Default" && !(profile.StartsWith("Profile ", StringComparison.Ordinal) && profile.All(c => char.IsLetterOrDigit(c) || c == ' ')))
        {
            profile = "Default";
            try
            {
                using var stream = new FileStream(Path.Combine(edgeData, "Local State"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var state = JsonDocument.Parse(stream);
                if (state.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("last_used", out var last) && last.GetString() is { } value &&
                    (value == "Default" || value.StartsWith("Profile ", StringComparison.Ordinal) && value.All(c => char.IsLetterOrDigit(c) || c == ' '))) profile = value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        string source = Path.Combine(edgeData, profile, "Favicons");
        var urls = EnumerateUrls(snapshot).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) return result;
        var edge = ReadCache(source, urls, MaxIconBytes, cancellation);
        var candidates = urls.Where(url => !edge.Hits.TryGetValue(url, out var hit) || !hit.Exact).ToArray();
        string localSource = Path.Combine(BrowserWindow.ResolveDataDir(), "profile", "EBWebView", "Default", "Favicons");
        var local = candidates.Length == 0 || edge.Bytes >= MaxIconBytes
            ? new CacheRead(new Dictionary<string, IconHit>(StringComparer.Ordinal), 0)
            : ReadCache(localSource, candidates, MaxIconBytes - edge.Bytes, cancellation);
        foreach (string url in urls)
        {
            edge.Hits.TryGetValue(url, out var edgeHit);
            local.Hits.TryGetValue(url, out var localHit);
            var chosen = edgeHit is { Exact: true } ? edgeHit : localHit is { Exact: true } ? localHit : edgeHit ?? localHit;
            if (chosen is not null) result[url] = chosen.Data;
        }
        return result;
    }

    private static CacheRead ReadCache(string source, IEnumerable<string> urls, int byteBudget, CancellationToken cancellation)
    {
        var result = new Dictionary<string, IconHit>(StringComparer.Ordinal);
        int total = 0;
        string? temporary = null; IntPtr db = IntPtr.Zero, statement = IntPtr.Zero, originStatement = IntPtr.Zero;
        try
        {
            if (byteBudget <= 0 || !File.Exists(source) || new FileInfo(source).Length > 128L * 1024 * 1024) return new CacheRead(result, total);
            temporary = Path.GetTempFileName();
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            }
            cancellation.ThrowIfCancellationRequested();
            if (sqlite3_open_v2(temporary, out db, 1, IntPtr.Zero) != SqliteOk) return new CacheRead(result, total);
            const string sql = "SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id=m.icon_id WHERE m.page_url=?1 AND length(b.image_data) BETWEEN 1 AND 131072 ORDER BY b.width DESC LIMIT 1";
            if (sqlite3_prepare_v2(db, sql, -1, out statement, IntPtr.Zero) != SqliteOk) return new CacheRead(result, total);
            const string originSql = "SELECT b.image_data FROM icon_mapping m JOIN favicon_bitmaps b ON b.icon_id=m.icon_id WHERE m.page_url>=?1 AND m.page_url<?2 AND length(b.image_data) BETWEEN 1 AND 131072 ORDER BY m.page_url, b.width DESC LIMIT 1";
            if (sqlite3_prepare_v2(db, originSql, -1, out originStatement, IntPtr.Zero) != SqliteOk)
            {
                if (originStatement != IntPtr.Zero) sqlite3_finalize(originStatement);
                originStatement = IntPtr.Zero;
            }
            var origins = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            byte[]? ReadIcon(IntPtr query, string first, string? second = null)
            {
                try
                {
                    if (sqlite3_bind_text(query, 1, first, -1, new IntPtr(-1)) != SqliteOk) return null;
                    if (second is not null && sqlite3_bind_text(query, 2, second, -1, new IntPtr(-1)) != SqliteOk) return null;
                    if (sqlite3_step(query) != SqliteRow) return null;
                    int length = sqlite3_column_bytes(query, 0); IntPtr blob = sqlite3_column_blob(query, 0);
                    if (length is not (> 0 and <= 131072) || blob == IntPtr.Zero) return null;
                    var bytes = new byte[length]; Marshal.Copy(blob, bytes, 0, length); return bytes;
                }
                finally { sqlite3_reset(query); sqlite3_clear_bindings(query); }
            }
            foreach (string url in urls)
            {
                cancellation.ThrowIfCancellationRequested();
                if (total >= byteBudget) break;
                byte[]? bytes = ReadIcon(statement, url);
                bool exact = bytes is not null;
                if (bytes is not null)
                {
                    if (total + bytes.Length > byteBudget) break;
                    total += bytes.Length;
                }
                else if (originStatement != IntPtr.Zero && TryOriginPrefix(url, out var prefix))
                {
                    if (!origins.TryGetValue(prefix, out bytes))
                    {
                        // The trailing '/' is immediately followed by '0' in
                        // UTF-8 ordering, bounding this exact origin's paths.
                        bytes = ReadIcon(originStatement, prefix, prefix[..^1] + "0");
                        if (bytes is not null && total + bytes.Length > byteBudget) break;
                        origins[prefix] = bytes;
                        if (bytes is not null) total += bytes.Length;
                    }
                }
                if (bytes is not null) result[url] = new IconHit(bytes, exact);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (originStatement != IntPtr.Zero) sqlite3_finalize(originStatement);
            if (db != IntPtr.Zero) sqlite3_close(db);
            if (temporary != null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new CacheRead(result, total);
    }

    private static bool TryOriginPrefix(string value, out string prefix)
    {
        prefix = "";
        if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || string.IsNullOrEmpty(uri.Host)) return false;
        prefix = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";
        return true;
    }

    internal static bool SameOrigin(string first, string second) =>
        TryOriginPrefix(first, out var left) && TryOriginPrefix(second, out var right) && string.Equals(left, right, StringComparison.Ordinal);

    internal static bool TryPageKey(string value, out string key)
    {
        key = "";
        if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.UserInfo.Length != 0) return false;
        key = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return true;
    }

    internal static Dictionary<string, List<string>> BuildPageIndex(FavoriteSnapshot snapshot, CancellationToken cancellation)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (string url in EnumerateUrls(snapshot))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > 100_000) return new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!seen.Add(url)) continue;
            if (!TryPageKey(url, out var key)) continue;
            if (!result.TryGetValue(key, out var aliases)) result[key] = aliases = [];
            aliases.Add(url);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateUrls(FavoriteSnapshot snapshot)
    {
        IEnumerable<FavoriteNode> Walk(IEnumerable<FavoriteNode> nodes)
        {
            foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        return Walk(snapshot.EnumerateRoots()).Select(node => node.Url).OfType<string>();
    }
}
