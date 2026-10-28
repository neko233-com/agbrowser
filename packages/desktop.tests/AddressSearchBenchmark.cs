using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgBrowser.Desktop;

internal static class AddressSearchBenchmark
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        VerifyBehavior(Path.Combine(directory, "behavior"));
        string fixture = Path.Combine(directory, "benchmark");
        Directory.CreateDirectory(fixture);
        var entries = Enumerable.Range(0, 1000).Select(i => new {
            url = $"https://site{i % 300}.example.test/history/{i}", title = $"History {i}",
            visited = 1700000000000L + i, visits = i % 13 + 1
        }).ToArray();
        File.WriteAllText(Path.Combine(fixture, "history.json"), JsonSerializer.Serialize(new { version = 1, entries }));
        var history = new AddressHistory(fixture);
        var nodes = Enumerable.Range(0, 30000).Select(i => new FavoriteNode(i.ToString(), $"Resource {i:D5}", $"https://site{i % 300}.example.test/page/{i}", [])).ToList();
        nodes.Add(new FavoriteNode("folder", "Nested", null, [
            new FavoriteNode("g1", "Google", "https://www.google.com/", []),
            new FavoriteNode("g2", "Google query", "https://www.google.com/search?q=buildworld&source=bookmark", []),
            new FavoriteNode("port", "Port", "https://example.test:444/path", []),
            new FavoriteNode("v6", "IPv6", "https://[::1]/resource", []),
            new FavoriteNode("duplicate1", "", "https://duplicate.example.test/path", []),
            new FavoriteNode("duplicate2", "Duplicate title", "https://duplicate.example.test/path", [])
        ]));
        var snapshot = new FavoriteSnapshot("Default", nodes, null, "1");
        string[] queries = ["site1", "site12", "site123", "resource 123", "example.test/page/299", "notfound", "history 42", "site123.example", "goo", "duplicate", "[::1]", "example.test:444"];
        long beforeCache = GC.GetTotalMemory(true);
        var cold = Stopwatch.StartNew();
        _ = history.Search(queries[0], snapshot);
        cold.Stop();
        long retained = GC.GetTotalMemory(true) - beforeCache;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var results = queries.Select(query => history.Search(query, snapshot)).ToArray();
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        string serialized = JsonSerializer.Serialize(results);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        if (Environment.GetEnvironmentVariable("AGB_SEARCH_BENCH_OUTPUT") is { Length: > 0 } output) File.WriteAllText(output, serialized);
        Console.WriteLine($"PASS address behavior; 30000 favorites, first query {cold.ElapsedMilliseconds} ms, {queries.Length} later queries {watch.ElapsedMilliseconds} ms, allocated {allocated / 1024} KiB, retained change {retained / 1024} KiB, results SHA256 {hash}");
        VerifyCacheBound(Path.Combine(directory, "large"));
    }

    private static void VerifyBehavior(string directory)
    {
        var history = new AddressHistory(directory);
        history.Record("https://www.google.com/", "Google");
        history.Record("https://www.google.com/search?q=buildworld&source=1", "buildworld - Google 搜索");
        history.Record("https://www.google.com/search?q=buildworld&source=2", "buildworld - Google 搜索");
        var google = new FavoriteNode("1", "Google bookmark", "https://www.google.com/", []);
        var invalid = new FavoriteNode("2", "private", "https://user:pass@example.test/", []);
        var github = new FavoriteNode("3", "GitHub project", "https://github.com/openai/", []);
        var snapshot = new FavoriteSnapshot("Default", [google, invalid, github], null, "1");
        var matches = history.Search("goo", snapshot);
        Assert(matches.Any(item => item.Url == "https://www.google.com/" && item.Favorite && item.Visits >= 1), "history and bookmark are merged at the root");
        Assert(matches.Count(item => item.Url.Contains("/search?q=buildworld", StringComparison.Ordinal)) == 1, "duplicate Google search URLs are collapsed");
        Assert(history.Search("GITHUB", snapshot).Any(item => item.Url == "https://github.com/" && item.Favorite), "site root is suggested case insensitively");
        Assert(history.Search("private", snapshot).All(item => !item.Url.Contains("user:pass", StringComparison.Ordinal)), "credential URLs are excluded");
        Assert(history.Search("  ", snapshot).Count == 0 && history.Search("goo", snapshot, 0).Count == 0, "empty query and zero limit return no matches");
        history.Record("https://fresh.example.test/page", "Freshly visited");
        Assert(history.Search("fresh", snapshot).Any(item => item.Url == "https://fresh.example.test/page"), "new visits update suggestions");
        var changed = new FavoriteSnapshot("Default", [new FavoriteNode("4", "New favorite", "https://new-favorite.example.test/", [])], null, "1");
        Assert(history.Search("new-favorite", changed).Any(item => item.Url == "https://new-favorite.example.test/"), "replaced bookmark snapshot updates suggestions");
        var oldSearch = history.SearchAsync("github", snapshot, CancellationToken.None);
        var newSearch = history.SearchAsync("new-favorite", changed, CancellationToken.None);
        Assert(oldSearch.GetAwaiter().GetResult().Any(item => item.Url == "https://github.com/"), "parallel old snapshot remains correct");
        Assert(newSearch.GetAwaiter().GetResult().Any(item => item.Url == "https://new-favorite.example.test/"), "parallel new snapshot remains correct");
        var beforeVisit = history.SearchAsync("late-visit", snapshot, CancellationToken.None);
        history.Record("https://late-visit.example.test/page", "Late visit");
        var afterVisit = history.SearchAsync("late-visit", snapshot, CancellationToken.None);
        Assert(beforeVisit.GetAwaiter().GetResult().All(item => item.Url != "https://late-visit.example.test/page"), "an in-flight query uses its captured history");
        Assert(afterVisit.GetAwaiter().GetResult().Any(item => item.Url == "https://late-visit.example.test/page"), "a newer query sees the recorded visit");
        Assert(history.Search("late-visit", snapshot).Any(item => item.Url == "https://late-visit.example.test/page"), "an old query does not replace newer history");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { history.SearchAsync("goo", snapshot, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Cancelled search completed normally"); }
        catch (OperationCanceledException) { }
    }

    private static void VerifyCacheBound(string directory)
    {
        var cache = typeof(AddressHistory).GetField("cachedCorpus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (cache is null) return; // The pre-cache build is used for benchmark comparison.
        var history = new AddressHistory(directory);
        var nodes = Enumerable.Range(0, 100001).Select(i => new FavoriteNode(i.ToString(), "", $"https://large.example.test/{i}", [])).ToArray();
        var snapshot = new FavoriteSnapshot("Default", nodes, null, "1");
        Assert(history.Search("no-match", snapshot, 1).Count == 0, "large snapshot remains searchable");
        Assert(cache.GetValue(history) is null, "oversized index is not retained");
        Console.WriteLine("PASS oversized address index is not retained");
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
