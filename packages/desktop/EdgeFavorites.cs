using System.Text.Json;

namespace AgBrowser.Desktop;

internal sealed record FavoriteNode(string Id, string Name, string? Url, IReadOnlyList<FavoriteNode> Children, bool CanModify = true);
internal sealed record FavoriteSnapshot(string Profile, IReadOnlyList<FavoriteNode> Bar, FavoriteNode? Other, string BarId, bool Available = true, long ConnectionGeneration = 0, string? ProfileId = null, IReadOnlyList<FavoriteNode>? AdditionalRoots = null)
{
    internal IEnumerable<FavoriteNode> EnumerateRoots()
    {
        foreach (var node in Bar) yield return node;
        if (Other is not null) yield return Other;
        if (AdditionalRoots is not null) foreach (var root in AdditionalRoots) yield return root;
    }
}

/// <summary>Reads Edge's active profile without writing its files or disturbing its process.</summary>
internal sealed class EdgeFavoritesReader
{
    private readonly string userData = Environment.GetEnvironmentVariable("AGB_EDGE_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
    private string? fingerprint;
    private readonly object fingerprintLock = new();
    private int generation;
    private string? stateFingerprint;
    private string cachedProfile = "Default";

    public async Task<FavoriteSnapshot?> ReadIfChangedAsync()
    {
        int startedGeneration;
        lock (fingerprintLock) startedGeneration = generation;
        return await Task.Run(() =>
        {
            try
            {
                string localState = Path.Combine(userData, "Local State");
                bool stateExists;
                try { _ = File.GetAttributes(localState); stateExists = true; }
                catch (FileNotFoundException) { stateExists = false; }
                catch (DirectoryNotFoundException) { stateExists = false; }
                if (stateExists)
                {
                    var stateInfo = new FileInfo(localState);
                    string stamp = $"{stateInfo.Length}|{stateInfo.LastWriteTimeUtc.Ticks}";
                    if (stamp != stateFingerprint)
                    {
                        using var state = ReadJson(localState);
                        string candidate = "Default";
                        if (state.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("last_used", out var last) && last.ValueKind == JsonValueKind.String)
                            candidate = last.GetString() ?? "Default";
                        cachedProfile = candidate == "Default" || (candidate.StartsWith("Profile ", StringComparison.Ordinal) && candidate.All(c => char.IsLetterOrDigit(c) || c == ' ')) ? candidate : "";
                        stateFingerprint = stamp;
                    }
                }
                else { cachedProfile = "Default"; stateFingerprint = null; }
                string profile = cachedProfile;
                if (profile.Length == 0) return MissingSnapshot("未知配置", false, startedGeneration);
                string bookmarks = Path.Combine(userData, profile, "Bookmarks");
                try { _ = File.GetAttributes(bookmarks); }
                catch (FileNotFoundException) { return MissingSnapshot(profile, Directory.Exists(Path.GetDirectoryName(bookmarks)!), startedGeneration); }
                catch (DirectoryNotFoundException) { return MissingSnapshot(profile, false, startedGeneration); }
                var info = new FileInfo(bookmarks);
                string current = $"{profile}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                lock (fingerprintLock) if (current == fingerprint) return null;
                using var document = ReadJson(bookmarks);
                var roots = document.RootElement.GetProperty("roots");
                var bar = ReadChildren(roots.GetProperty("bookmark_bar"), 0);
                FavoriteNode? other = null;
                if (roots.TryGetProperty("other", out var otherRoot))
                {
                    var items = ReadChildren(otherRoot, 0);
                    other = new FavoriteNode(GetString(otherRoot, "id"), "其他收藏夹", null, items);
                }
                var additional = new List<FavoriteNode>();
                foreach (var entry in roots.EnumerateObject())
                {
                    if (entry.Name is "bookmark_bar" or "other" || entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var items = ReadChildren(entry.Value, 0);
                    if (items.Count == 0) continue;
                    string name = GetString(entry.Value, "name");
                    if (string.IsNullOrWhiteSpace(name)) name = entry.Name == "synced" ? "移动设备收藏" : "其他收藏";
                    additional.Add(new FavoriteNode(GetString(entry.Value, "id"), name, null, items));
                }
                lock (fingerprintLock)
                {
                    if (generation != startedGeneration) return null;
                    fingerprint = current;
                }
                return new FavoriteSnapshot(profile, bar, other, GetString(roots.GetProperty("bookmark_bar"), "id"), AdditionalRoots: additional);
            }
            // Do not keep presenting another profile's old favorites as the
            // current profile when Edge is temporarily replacing a file or its
            // data cannot be read. The next poll will retry the read.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
            { return MissingSnapshot(cachedProfile.Length == 0 ? "未知配置" : cachedProfile, false, startedGeneration); }
        });
    }

    private FavoriteSnapshot? MissingSnapshot(string profile, bool available, int startedGeneration)
    {
        string current = $"{profile}|{(available ? "empty" : "unavailable")}";
        lock (fingerprintLock)
        {
            if (generation != startedGeneration || fingerprint == current) return null;
            fingerprint = current;
        }
        return new FavoriteSnapshot(profile, [], null, "", available);
    }

    internal void Reset() { lock (fingerprintLock) { generation++; fingerprint = null; } }
    internal static FavoriteSnapshot? ReadApiSnapshot(JsonElement tree, long generation, string? profileId)
    {
        if (tree.ValueKind != JsonValueKind.Array || tree.GetArrayLength() == 0 ||
            !tree[0].TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return null;
        var roots = children.EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object && GetString(node, "id").Length > 0).ToArray();
        if (roots.Length == 0) return null;
        string Kind(JsonElement node)
        {
            string type = GetString(node, "folderType");
            if (type.Length > 0) return type;
            return GetString(node, "id") switch { "1" => "bookmarks-bar", "2" => "other", "3" => "mobile", _ => "" };
        }
        var bars = roots.Where(node => Kind(node) == "bookmarks-bar").ToArray();
        var primary = bars.FirstOrDefault(node => !IsUnmodifiable(node));
        if (primary.ValueKind == JsonValueKind.Undefined) primary = bars.FirstOrDefault();
        var other = roots.FirstOrDefault(node => Kind(node) == "other");
        var counts = roots.GroupBy(Kind).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string Name(JsonElement node)
        {
            string kind = Kind(node);
            string name = kind switch { "bookmarks-bar" => "收藏栏", "other" => "其他收藏夹", "mobile" => "移动设备收藏", _ => GetString(node, "title") };
            if (string.IsNullOrWhiteSpace(name)) name = "其他收藏";
            if (kind.Length > 0 && counts[kind] > 1 &&
                node.TryGetProperty("syncing", out var syncing) && syncing.ValueKind is JsonValueKind.True or JsonValueKind.False)
                name += syncing.GetBoolean() ? "（已同步）" : "（未同步）";
            return name;
        }
        FavoriteNode Root(JsonElement node)
        {
            bool writable = !IsUnmodifiable(node);
            return new FavoriteNode(GetString(node, "id"), Name(node), null, ReadApiChildren(node, 0, !writable), writable);
        }
        var additional = new List<FavoriteNode>();
        foreach (var node in roots)
        {
            if (node.Equals(primary) || node.Equals(other)) continue;
            additional.Add(Root(node));
        }
        string barId = primary.ValueKind != JsonValueKind.Undefined && !IsUnmodifiable(primary) ? GetString(primary, "id") : "";
        return new FavoriteSnapshot("Edge 连接", primary.ValueKind == JsonValueKind.Undefined ? [] : ReadApiChildren(primary, 0, IsUnmodifiable(primary)),
            other.ValueKind == JsonValueKind.Undefined ? null : Root(other), barId, ConnectionGeneration: generation, ProfileId: profileId, AdditionalRoots: additional);
    }

    private static bool IsUnmodifiable(JsonElement node) => node.TryGetProperty("unmodifiable", out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False);

    internal static IReadOnlyList<FavoriteNode> ReadApiChildren(JsonElement folder, int depth, bool inheritedReadOnly = false)
    {
        if (depth >= 24 || !folder.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return [];
        var result = new List<FavoriteNode>();
        foreach (var item in children.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string id = GetString(item, "id"), name = GetString(item, "title"), url = GetString(item, "url");
            bool writable = !inheritedReadOnly && !IsUnmodifiable(item);
            if (url.Length == 0) result.Add(new FavoriteNode(id, name, null, ReadApiChildren(item, depth + 1, !writable), writable));
            else result.Add(new FavoriteNode(id, name, url, [], writable));
        }
        return result;
    }

    private static JsonDocument ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonDocument.Parse(stream);
    }
    private static string GetString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static IReadOnlyList<FavoriteNode> ReadChildren(JsonElement folder, int depth)
    {
        if (depth >= 24 || !folder.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return [];
        var result = new List<FavoriteNode>();
        foreach (var item in children.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string type = GetString(item, "type"), name = GetString(item, "name"), id = GetString(item, "id");
            if (type == "folder") result.Add(new FavoriteNode(id, name, null, ReadChildren(item, depth + 1)));
            else if (type == "url")
            {
                var url = GetString(item, "url");
                if (url.Length > 0) result.Add(new FavoriteNode(id, name, url, []));
            }
        }
        return result;
    }
}
