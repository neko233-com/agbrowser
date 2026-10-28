using System.Text.Json;

namespace AgBrowser.Desktop;

internal sealed record AddressSuggestion(string Title, string Url, bool Favorite, long Visited, int Visits, bool Primary = false, bool Search = false)
{
    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Title) ? Url : $"{Title} — {Url}";
    }
}

/// <summary>Local browsing suggestions; unreadable history is never overwritten.</summary>
internal sealed class AddressHistory
{
    private sealed record Entry(string Url, string Title, long Visited, int Visits);
    private readonly string path;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool readable = true, dirty;
    private long historyRevision, searchRequest, cachedRequest;
    private readonly object corpusLock = new();
    private SearchCorpus? cachedCorpus;
    private const int MaxEntries = 2000;
    private const int MaxCachedCandidates = 100000;

    public AddressHistory(string dataDir)
    {
        path = Path.Combine(dataDir, "history.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new JsonException("Unsupported history version");
            foreach (var item in root.GetProperty("entries").EnumerateArray().Take(MaxEntries))
            {
                string? url = item.GetProperty("url").GetString();
                if (!TryUrl(url, out var normalized)) continue;
                string title = item.TryGetProperty("title", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
                long visited = item.TryGetProperty("visited", out var stamp) && stamp.TryGetInt64(out long value) ? value : 0;
                int visits = item.TryGetProperty("visits", out var count) && count.TryGetInt32(out int times) ? Math.Clamp(times, 1, 1000000) : 1;
                entries[normalized] = new Entry(normalized, title.Length > 512 ? title[..512] : title, visited, visits);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { readable = false; entries.Clear(); }
    }

    private static bool TryUrl(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        normalized = uri.AbsoluteUri; return true;
    }
    public bool Record(string? url, string? title, bool countVisit = true)
    {
        if (!readable || !TryUrl(url, out var normalized)) return false;
        title ??= "";
        if (title.Length > 512) title = title[..512];
        entries.TryGetValue(normalized, out var previous);
        if (!countVisit && previous is null) return false;
        int visits = countVisit ? Math.Min((previous?.Visits ?? 0) + 1, 1000000) : previous!.Visits;
        long visited = countVisit ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : previous!.Visited;
        entries[normalized] = new Entry(normalized, title, visited, visits);
        if (entries.Count > MaxEntries)
            foreach (var oldest in entries.Values.OrderBy(x => x.Visited).Take(entries.Count - MaxEntries).ToArray()) entries.Remove(oldest.Url);
        historyRevision++;
        dirty = true; return true;
    }
    public void Save()
    {
        if (!readable || !dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, entries = entries.Values.OrderByDescending(x => x.Visited).Select(x => new { url = x.Url, title = x.Title, visited = x.Visited, visits = x.Visits }) }));
            File.Move(temporary, path, true); dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public IReadOnlyList<AddressSuggestion> Search(string input, FavoriteSnapshot? favorites, int limit = 8)
        => SearchSnapshot(input, favorites, limit, entries.Values.ToArray(), historyRevision, Interlocked.Increment(ref searchRequest), CancellationToken.None);

    public Task<IReadOnlyList<AddressSuggestion>> SearchAsync(string input, FavoriteSnapshot? favorites, CancellationToken cancellation, int limit = 8)
    {
        // Copy the mutable history on the UI thread; favorite snapshots are
        // immutable once published. The potentially large search runs away
        // from the message pump.
        var history = entries.Values.ToArray();
        long revision = historyRevision, request = Interlocked.Increment(ref searchRequest);
        return Task.Run(() => SearchSnapshot(input, favorites, limit, history, revision, request, cancellation), cancellation);
    }

    private sealed record IndexedSuggestion(AddressSuggestion Item, string Host, string MatchHost, string Site);

    private sealed class SearchCorpus(FavoriteSnapshot? favorites, long revision, IndexedSuggestion[] raw)
    {
        private readonly object expandedLock = new();
        private IndexedSuggestion[]? expanded;
        public FavoriteSnapshot? Favorites { get; } = favorites;
        public long Revision { get; } = revision;
        public IndexedSuggestion[] Raw { get; } = raw;

        public IndexedSuggestion[] WithSiteRoots(CancellationToken cancellation)
        {
            lock (expandedLock) if (expanded is not null) return expanded;
            var candidates = new Dictionary<string, IndexedSuggestion>(Raw.Length, StringComparer.Ordinal);
            foreach (var item in Raw) { cancellation.ThrowIfCancellationRequested(); candidates.Add(item.Item.Url, item); }
            foreach (var site in Raw.GroupBy(item => { cancellation.ThrowIfCancellationRequested(); return item.Site; }, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                var pages = site.ToArray();
                int visits = (int)Math.Min(1000000L, pages.Sum(item => (long)item.Item.Visits));
                long visited = pages.Max(item => item.Item.Visited);
                if (candidates.TryGetValue(site.Key, out var root))
                    candidates[site.Key] = root with { Item = root.Item with { Visits = visits, Visited = visited } };
                else
                {
                    var first = pages[0];
                    var item = new AddressSuggestion(first.Host, site.Key, pages.Any(page => page.Item.Favorite), visited, visits);
                    candidates[site.Key] = new IndexedSuggestion(item, first.Host, first.MatchHost, site.Key);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            var result = candidates.Values.ToArray();
            lock (expandedLock) return expanded ??= result;
        }
    }

    private SearchCorpus GetCorpus(FavoriteSnapshot? favorites, Entry[] history, long revision, long request, CancellationToken cancellation)
    {
        lock (corpusLock)
            if (cachedCorpus is { } existing && existing.Revision == revision && ReferenceEquals(existing.Favorites, favorites))
            { cachedRequest = Math.Max(cachedRequest, request); return existing; }

        var candidates = history.ToDictionary(x => x.Url, x => new AddressSuggestion(x.Title, x.Url, false, x.Visited, x.Visits), StringComparer.Ordinal);
        void AddFavorites(IEnumerable<FavoriteNode> nodes, int depth)
        {
            if (depth > 24) return;
            foreach (var node in nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                if (TryUrl(node.Url, out var url))
                {
                    if (candidates.TryGetValue(url, out var existing)) candidates[url] = existing with { Favorite = true, Title = string.IsNullOrWhiteSpace(existing.Title) ? node.Name : existing.Title };
                    else candidates[url] = new AddressSuggestion(node.Name, url, true, 0, 0);
                }
                else if (node.Url is null) AddFavorites(node.Children, depth + 1);
            }
        }
        if (favorites != null)
        {
            AddFavorites(favorites.Bar, 0);
            if (favorites.Other != null) AddFavorites(favorites.Other.Children, 0);
            if (favorites.AdditionalRoots != null)
                foreach (var root in favorites.AdditionalRoots) AddFavorites(root.Children, 0);
        }
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        var sites = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Share(Dictionary<string, string> pool, string value)
        {
            if (pool.TryGetValue(value, out var shared)) return shared;
            pool.Add(value, value); return value;
        }
        var raw = new IndexedSuggestion[candidates.Count]; int index = 0;
        foreach (var item in candidates.Values)
        {
            cancellation.ThrowIfCancellationRequested();
            var uri = new Uri(item.Url);
            string host = Share(hosts, uri.Host);
            string matchHost = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? Share(hosts, host[4..]) : host;
            string site = Share(sites, uri.GetLeftPart(UriPartial.Authority) + "/");
            raw[index++] = new IndexedSuggestion(item, host, matchHost, site);
        }
        cancellation.ThrowIfCancellationRequested();
        var created = new SearchCorpus(favorites, revision, raw);
        lock (corpusLock)
        {
            if (cachedCorpus is { } existing && existing.Revision == revision && ReferenceEquals(existing.Favorites, favorites))
            { cachedRequest = Math.Max(cachedRequest, request); return existing; }
            // An older background request must not replace a newer index.
            if (request >= cachedRequest)
            {
                cachedCorpus = created.Raw.Length <= MaxCachedCandidates ? created : null;
                cachedRequest = request;
            }
        }
        return created;
    }

    private IReadOnlyList<AddressSuggestion> SearchSnapshot(string input, FavoriteSnapshot? favorites, int limit, Entry[] history, long revision, long request, CancellationToken cancellation)
    {
        string query = input.Trim();
        int count = Math.Clamp(limit, 0, 20);
        if (query.Length == 0 || query.Length > 512 || count == 0) return [];
        cancellation.ThrowIfCancellationRequested();
        var corpus = GetCorpus(favorites, history, revision, request, cancellation);
        // Short hostname queries can also suggest a site's root.
        var candidates = query.Any(c => char.IsWhiteSpace(c) || c is '/' or ':' or '?' or '#') ? corpus.Raw : corpus.WithSiteRoots(cancellation);
        static int Match(IndexedSuggestion candidate, string query)
        {
            if (candidate.MatchHost.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (candidate.MatchHost.Contains(query, StringComparison.OrdinalIgnoreCase)) return 1;
            if (candidate.Item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
            return candidate.Item.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : int.MaxValue;
        }
        static string Identity(AddressSuggestion item)
        {
            var uri = new Uri(item.Url);
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            if (host.StartsWith("google.", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Equals("/search", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    int equals = part.IndexOf('=');
                    if (equals > 0 && part[..equals].Equals("q", StringComparison.OrdinalIgnoreCase))
                        return "search|" + host.ToLowerInvariant() + "|" + Uri.UnescapeDataString(part[(equals + 1)..].Replace('+', ' '));
                }
            }
            return item.Url;
        }
        var result = new List<AddressSuggestion>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Select(item => { cancellation.ThrowIfCancellationRequested(); return (item: item.Item, score: Match(item, query)); }).Where(x => x.score != int.MaxValue)
            .OrderBy(x => x.score).ThenByDescending(x => x.item.Visits).ThenByDescending(x => x.item.Visited).ThenByDescending(x => x.item.Favorite))
        {
            cancellation.ThrowIfCancellationRequested();
            if (seen.Add(Identity(candidate.item))) result.Add(candidate.item);
            if (result.Count == count) break;
        }
        return result;
    }
}
