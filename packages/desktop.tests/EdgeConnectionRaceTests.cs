using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgBrowser.Desktop;

internal static class EdgeConnectionRaceTests
{
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    public static async Task<int> Run(string directory)
    {
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("AGB_DESKTOP_DATA_DIR", directory);
        await VerifyOfflineRoots(directory);
        string pipeName = "agbrowser-edge-favorites-" + SingleInstance.ProfileHash(directory);
        var snapshots = Channel.CreateUnbounded<FavoriteSnapshot>();
        using var bridge = new EdgeFavoritesBridge();
        bridge.SnapshotReceived += snapshot => snapshots.Writer.TryWrite(snapshot);
        bridge.Start();
        using var first = await Connect(pipeName);
        await SendComplexSnapshot(first, 1, "Mobile original");
        var complex = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (complex.BarId != "account-bar" || complex.Bar.Single().Name != "Account favorite" || complex.Other?.Id != "account-other" || complex.AdditionalRoots?.Count != 4)
            throw new InvalidOperationException("Bookmark root types or nonstandard IDs were not preserved");
        var managed = complex.AdditionalRoots.Single(root => root.Id == "managed");
        if (managed.CanModify || managed.Children.Single().CanModify || managed.Children.Single().Children.Single().CanModify)
            throw new InvalidOperationException("Managed read-only status was not inherited");
        var history = new AddressHistory(directory);
        if (!history.Search("phone-site", complex).Any(item => item.Url == "https://phone-site.example.test/"))
            throw new InvalidOperationException("Additional-root bookmarks were not included in address suggestions");
        await SendComplexSnapshot(first, 2, "Mobile updated");
        var changedRoot = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (changedRoot.AdditionalRoots!.Single(root => root.Id == "mobile-root").Children.Single().Name != "Mobile updated")
            throw new InvalidOperationException("Changes confined to an additional root were ignored");
        await SendComplexSnapshot(first, 3, "Mobile updated", managedReadOnly: false);
        var changedPolicy = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (!changedPolicy.AdditionalRoots!.Single(root => root.Id == "managed").Children.Single().CanModify)
            throw new InvalidOperationException("A changed managed policy was ignored");
        using (var noBarTree = JsonDocument.Parse("[{\"id\":\"0\",\"children\":[{\"id\":\"managed\",\"title\":\"Managed\",\"unmodifiable\":\"managed\",\"children\":[]}]}]"))
        {
            var noBar = EdgeFavoritesReader.ReadApiSnapshot(noBarTree.RootElement, 1, null);
            if (noBar is null || noBar.BarId.Length != 0 || noBar.AdditionalRoots?.Single().CanModify != false)
                throw new InvalidOperationException("A managed-only tree must not invent a writable bookmark bar");
        }
        await SendSnapshot(first, "Profile A", 4);
        var old = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await SendSnapshot(first, "Profile A", 5);
        await Task.Delay(200);
        if (snapshots.Reader.TryRead(out _)) throw new InvalidOperationException("An unchanged snapshot was dispatched again");
        await SendSnapshot(first, "Profile A edited", 6);
        var edited = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (edited.Bar[0].Name != "Profile A edited") throw new InvalidOperationException("Changed favorites were not dispatched");
        await SendSnapshot(first, "Out of order", 5);
        await Task.Delay(200);
        if (snapshots.Reader.TryRead(out _)) throw new InvalidOperationException("An older snapshot was dispatched");
        first.Dispose();
        await WaitUntil(() => !bridge.Connected, "first connection closed");
        using var second = await Connect(pipeName);
        await SendSnapshot(second, "Profile B");
        await SelectProfile(bridge, "Profile B");
        var current = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (old.Bar[0].Id != current.Bar[0].Id || old.Bar[0].Name == current.Bar[0].Name)
            throw new InvalidOperationException("Fixture did not model colliding bookmark IDs");
        if (old.ConnectionGeneration == 0 || current.ConnectionGeneration == old.ConnectionGeneration)
            throw new InvalidOperationException("Reconnected profiles did not receive distinct connection generations");
        using var reader = new StreamReader(second, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await ExpectChanged(bridge.ExecuteAsync(old.ConnectionGeneration, "remove", bookmarkId: old.Bar[0].Id));
        var valid = bridge.ExecuteAsync(current.ConnectionGeneration, "update", bookmarkId: current.Bar[0].Id, title: "Allowed");
        string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Second connection closed");
        using var request = JsonDocument.Parse(line);
        if (request.RootElement.GetProperty("op").GetString() != "update" || request.RootElement.GetProperty("bookmarkId").GetString() != current.Bar[0].Id)
            throw new InvalidOperationException("A stale request reached the second profile");
        string id = request.RootElement.GetProperty("id").GetString()!;
        await WriteLine(second, JsonSerializer.Serialize(new { type = "result", id, ok = true }));
        await valid.WaitAsync(TimeSpan.FromSeconds(5));
        var writerLock = (SemaphoreSlim)typeof(EdgeFavoritesBridge).GetField("writerLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(bridge)!;
        await writerLock.WaitAsync();
        bool lockHeld = true;
        Task queued;
        try
        {
            queued = bridge.ExecuteAsync(current.ConnectionGeneration, "remove", bookmarkId: current.Bar[0].Id);
            second.Dispose();
            await WaitUntil(() => !bridge.Connected, "second connection closed");
            using var third = await Connect(pipeName);
            await SendSnapshot(third, "Profile C");
            await SelectProfile(bridge, "Profile C");
            var latest = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            writerLock.Release(); lockHeld = false;
            await ExpectChanged(queued);
            using var thirdReader = new StreamReader(third, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var thirdValid = bridge.ExecuteAsync(latest.ConnectionGeneration, "create", parentId: latest.BarId, title: "Allowed in C");
            string thirdLine = await thirdReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Third connection closed");
            using var thirdRequest = JsonDocument.Parse(thirdLine);
            if (thirdRequest.RootElement.GetProperty("op").GetString() != "create") throw new InvalidOperationException("Queued request crossed into the third profile");
            await WriteLine(third, JsonSerializer.Serialize(new { type = "result", id = thirdRequest.RootElement.GetProperty("id").GetString(), ok = true }));
            await thirdValid.WaitAsync(TimeSpan.FromSeconds(5));
            var inFlight = bridge.ExecuteAsync(latest.ConnectionGeneration, "remove", bookmarkId: latest.Bar[0].Id);
            string sent = await thirdReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Third connection closed before request");
            using (var sentRequest = JsonDocument.Parse(sent))
                if (sentRequest.RootElement.GetProperty("op").GetString() != "remove") throw new InvalidOperationException("Expected an in-flight remove");
            third.Dispose();
            try { await inFlight.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Disconnected in-flight operation unexpectedly succeeded"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("可能已在 Edge 完成", StringComparison.Ordinal)) { }
            await WaitUntil(() => !bridge.Connected, "third connection closed");
            using var fourth = await Connect(pipeName);
            await SendSnapshot(fourth, "Profile D");
            await SelectProfile(bridge, "Profile D");
            var last = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            using var fourthReader = new StreamReader(fourth, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            var fourthValid = bridge.ExecuteAsync(last.ConnectionGeneration, "create", parentId: last.BarId, title: "Allowed in D");
            string fourthLine = await fourthReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Fourth connection closed");
            using var fourthRequest = JsonDocument.Parse(fourthLine);
            if (fourthRequest.RootElement.GetProperty("op").GetString() != "create") throw new InvalidOperationException("In-flight request was replayed to the fourth profile");
            await WriteLine(fourth, JsonSerializer.Serialize(new { type = "result", id = fourthRequest.RootElement.GetProperty("id").GetString(), ok = true }));
            await fourthValid.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (lockHeld) writerLock.Release(); }
        await WaitUntil(() => !bridge.Connected, "legacy connection closed");
        await VerifyMultipleProfiles(bridge, snapshots, pipeName, writerLock);
        bridge.Dispose();
        await bridge.WaitForStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await VerifyPersistedSelection(pipeName);
        Console.WriteLine("PASS duplicate and stale snapshots are ignored; old writes cannot cross Edge connections; in-flight uncertainty is reported without replay");
        Console.WriteLine("PASS simultaneous Edge profiles can be selected, stay isolated on reconnect, and reject duplicate identities");
        Console.WriteLine("PASS additional bookmark roots, read-only managed folders and their address suggestions are preserved");
        return 0;
    }

    private static async Task VerifyOfflineRoots(string directory)
    {
        string edge = Path.Combine(directory, "edge-roots"), profile = Path.Combine(edge, "Default");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(edge, "Local State"), "{\"profile\":{\"last_used\":\"Default\"}}");
        await File.WriteAllTextAsync(Path.Combine(profile, "Bookmarks"), JsonSerializer.Serialize(new {
            roots = new {
                bookmark_bar = new { id = "1", type = "folder", children = Array.Empty<object>() },
                other = new { id = "2", type = "folder", children = Array.Empty<object>() },
                synced = new { id = "3", name = "Mobile favorites", type = "folder", children = new[] {
                    new { id = "7", name = "Offline phone", type = "url", url = "https://offline-phone.example.test/" },
                    new { id = "8", name = "Same origin", type = "url", url = "https://offline-phone.example.test/fallback" },
                    new { id = "9", name = "Different scheme", type = "url", url = "http://offline-phone.example.test/page" },
                    new { id = "10", name = "Different port", type = "url", url = "https://offline-phone.example.test:8443/page" },
                    new { id = "11", name = "Default port", type = "url", url = "https://offline-phone.example.test:443/page" },
                    new { id = "12", name = "Different host", type = "url", url = "https://offline-phone.example.test.evil/page" },
                    new { id = "13", name = "Visited page", type = "url", url = "https://offline-phone.example.test/page" },
                    new { id = "14", name = "Local only", type = "url", url = "https://visited.example.test/page" }
                } }
            }
        }));
        byte[] icon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO8u7Z8AAAAASUVORK5CYII=");
        byte[] visitedIcon;
        using (var image = new Bitmap(2, 2))
        using (var stream = new MemoryStream()) { image.SetPixel(0, 0, Color.Red); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png); visitedIcon = stream.ToArray(); }
        VerifySavedPageIcons(Path.Combine(directory, "page-icon-cache"), visitedIcon);
        WriteIconFixture(Path.Combine(profile, "Favicons"), [("https://offline-phone.example.test/", icon)]);
        WriteIconFixture(Path.Combine(directory, "profile", "EBWebView", "Default", "Favicons"),
            [("https://offline-phone.example.test/page", visitedIcon), ("https://visited.example.test/page", visitedIcon)]);
        string? previous = Environment.GetEnvironmentVariable("AGB_EDGE_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", edge);
            var snapshot = await new EdgeFavoritesReader().ReadIfChangedAsync();
            if (snapshot?.AdditionalRoots?.Single().Children.First().Name != "Offline phone")
                throw new InvalidOperationException("Offline Edge mobile favorites were not read");
            var icons = FavoriteIcons.Read(snapshot, CancellationToken.None);
            if (!icons.TryGetValue("https://offline-phone.example.test/", out var loaded) || !loaded.SequenceEqual(icon))
                throw new InvalidOperationException("An additional-root favicon was not loaded from the isolated Edge cache");
            if (!icons.TryGetValue("https://offline-phone.example.test/fallback", out var sameOrigin) || !sameOrigin.SequenceEqual(icon))
                throw new InvalidOperationException("A cached same-origin favicon was not used for a different bookmark path");
            if (!icons.TryGetValue("https://offline-phone.example.test/page", out var visited) || !visited.SequenceEqual(visitedIcon) ||
                !icons.TryGetValue("https://visited.example.test/page", out var localOnly) || !localOnly.SequenceEqual(visitedIcon))
                throw new InvalidOperationException("Exact agbrowser cache icons did not take precedence over same-origin fallbacks");
            if (!icons.TryGetValue("https://offline-phone.example.test:443/page", out var defaultPort) || !defaultPort.SequenceEqual(icon))
                throw new InvalidOperationException("The default port was not normalized for favicon lookup");
            if (icons.ContainsKey("http://offline-phone.example.test/page") || icons.ContainsKey("https://offline-phone.example.test:8443/page") || icons.ContainsKey("https://offline-phone.example.test.evil/page"))
                throw new InvalidOperationException("An icon was reused across different schemes, ports or hosts");
        }
        finally { Environment.SetEnvironmentVariable("AGB_EDGE_DATA_DIR", previous); }
    }

    private static void VerifySavedPageIcons(string directory, byte[] icon)
    {
        Directory.CreateDirectory(directory);
        var cache = new FavoritePageIconCache(directory);
        if (cache.Snapshot().Count != 0) throw new InvalidOperationException("New favicon cache should be empty");
        for (int i = 0; i < 1025; i++) cache.Record($"https://cache.example.test/{i}", icon);
        cache.Save();
        var restored = new FavoritePageIconCache(directory).Snapshot();
        if (restored.Count != 1024 || !restored.Keys.All(key => key.Length == 64) || !restored.Values.All(value => value.SequenceEqual(icon)) ||
            File.ReadAllText(Path.Combine(directory, "favorite-page-icons.json")).Contains("cache.example.test", StringComparison.Ordinal))
            throw new InvalidOperationException("Visited favicon cache was not bounded or restored");
        string longDirectory = Path.Combine(directory, "long-urls");
        var longCache = new FavoritePageIconCache(longDirectory); _ = longCache.Snapshot();
        string longPath = new('a', 8000);
        for (int i = 0; i < 140; i++) longCache.Record($"https://cache.example.test/{longPath}{i:D3}", icon);
        longCache.Save();
        string longFile = Path.Combine(longDirectory, "favorite-page-icons.json");
        var longRestored = new FavoritePageIconCache(longDirectory).Snapshot();
        if (longRestored.Count != 140 || new FileInfo(longFile).Length > 4 * 1024 * 1024 || File.ReadAllText(longFile).Contains(longPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Long bookmark URLs exceeded the bounded cache file");
        string legacyDirectory = Path.Combine(directory, "legacy"); Directory.CreateDirectory(legacyDirectory);
        string legacyPath = Path.Combine(legacyDirectory, "favorite-page-icons.json"), legacyUrl = "https://cache.example.test/legacy";
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new { version = 1, entries = new[] { new { url = legacyUrl, icon = Convert.ToBase64String(icon), updated = DateTime.UtcNow.Ticks } } }));
        var migrated = new FavoritePageIconCache(legacyDirectory);
        if (!migrated.Snapshot().ContainsKey(FavoritePageIconCache.HashPageKey(legacyUrl))) throw new InvalidOperationException("Legacy icon cache was not read");
        migrated.Save();
        if (File.ReadAllText(legacyPath).Contains(legacyUrl, StringComparison.Ordinal)) throw new InvalidOperationException("Legacy cache URL was not removed after migration");
        string path = Path.Combine(directory, "favorite-page-icons.json"), unknown = "{\"version\":999,\"entries\":[]}";
        File.WriteAllText(path, unknown);
        var future = new FavoritePageIconCache(directory);
        _ = future.Snapshot();
        if (future.Record("https://cache.example.test/new", icon)) throw new InvalidOperationException("Unknown cache format was treated as writable");
        future.Save();
        if (File.ReadAllText(path) != unknown) throw new InvalidOperationException("Unknown favicon cache was overwritten");
    }

    private static void WriteIconFixture(string path, (string Url, byte[] Icon)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (sqlite3_open_v2(path, out var db, 2 | 4, IntPtr.Zero) != 0)
        {
            if (db != IntPtr.Zero) sqlite3_close(db);
            throw new InvalidOperationException("Could not create the isolated favicon fixture");
        }
        try
        {
            string sql = "CREATE TABLE icon_mapping(page_url TEXT, icon_id INTEGER); CREATE TABLE favicon_bitmaps(icon_id INTEGER, image_data BLOB, width INTEGER); ";
            for (int i = 0; i < entries.Length; i++)
                sql += $"INSERT INTO icon_mapping VALUES('{entries[i].Url.Replace("'", "''")}', {i + 1}); INSERT INTO favicon_bitmaps VALUES({i + 1}, X'{Convert.ToHexString(entries[i].Icon)}', 16);";
            if (sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0) throw new InvalidOperationException("Could not populate the favicon fixture");
        }
        finally { sqlite3_close(db); }
    }

    private static Task SendComplexSnapshot(Stream stream, int sequence, string mobileTitle, bool managedReadOnly = true) => WriteLine(stream, JsonSerializer.Serialize(new {
        type = "snapshot", sequence,
        tree = new[] { new { id = "0", children = new object[] {
            new { id = "account-bar", folderType = "bookmarks-bar", syncing = true, children = new[] {
                new { id = "a", title = "Account favorite", url = "https://account.example.test/" }
            } },
            new { id = "account-other", folderType = "other", syncing = true, children = Array.Empty<object>() },
            new { id = "local-bar", folderType = "bookmarks-bar", syncing = false, children = new[] {
                new { id = "b", title = "Local favorite", url = "https://local.example.test/" }
            } },
            new { id = "local-other", folderType = "other", syncing = false, children = Array.Empty<object>() },
            new { id = "mobile-root", folderType = "mobile", children = new[] {
                new { id = "c", title = mobileTitle, url = "https://phone-site.example.test/" }
            } },
            new { id = "managed", title = "Managed favorites", unmodifiable = managedReadOnly ? "managed" : null, children = new[] {
                new { id = "managed-folder", title = "Folder", children = new[] {
                    new { id = "managed-link", title = "Managed link", url = "https://managed.example.test/" }
                } }
            } }
        } } }
    }));

    private static async Task VerifyPersistedSelection(string pipeName)
    {
        const string a = "10000000-0000-4000-8000-000000000001";
        const string b = "20000000-0000-4000-8000-000000000002";
        var snapshots = Channel.CreateUnbounded<FavoriteSnapshot>();
        using var restored = new EdgeFavoritesBridge();
        restored.SnapshotReceived += snapshot => snapshots.Writer.TryWrite(snapshot);
        restored.Start();
        using var wrong = await Connect(pipeName);
        await SendSnapshot(wrong, "Profile A after restart", profileId: a);
        await WaitUntil(() => restored.GetConnections().Count == 1, "unselected profile after restart");
        if (restored.Connected || snapshots.Reader.TryRead(out _)) throw new InvalidOperationException("Wrong profile was selected after restart");
        using var right = await Connect(pipeName);
        await SendSnapshot(right, "Profile B after restart", profileId: b);
        var current = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (current.ProfileId != b || !restored.IsCurrentConnection(current.ConnectionGeneration))
            throw new InvalidOperationException("Saved profile was not restored after restart");
        string preference = Path.Combine(Environment.GetEnvironmentVariable("AGB_DESKTOP_DATA_DIR")!, "edge-favorites-profile.json");
        string saved = File.ReadAllText(preference), incompatible = "{\"version\":999,\"profileId\":\"future-format\"}";
        try
        {
            File.WriteAllText(preference, incompatible);
            using var unknown = new EdgeFavoritesBridge();
            if (!unknown.WaitingForSelection) throw new InvalidOperationException("Unknown preference did not require a manual selection");
            var save = typeof(EdgeFavoritesBridge).GetMethod("SaveSelection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            if ((bool)save.Invoke(unknown, [b])! || File.ReadAllText(preference) != incompatible)
                throw new InvalidOperationException("Unknown profile settings were overwritten");
        }
        finally { File.WriteAllText(preference, saved); }
    }

    private static async Task VerifyMultipleProfiles(EdgeFavoritesBridge bridge, Channel<FavoriteSnapshot> snapshots, string pipeName, SemaphoreSlim writerLock)
    {
        const string a = "10000000-0000-4000-8000-000000000001";
        const string b = "20000000-0000-4000-8000-000000000002";
        using var first = await Connect(pipeName);
        await SendSnapshot(first, "Profile A", profileId: a);
        await SelectProfile(bridge, "Profile A");
        var original = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        using var second = await Connect(pipeName);
        await SendSnapshot(second, "Profile B", profileId: b);
        await WaitUntil(() => bridge.GetConnections().Count == 2, "both profiles visible");
        var secondChoice = bridge.GetConnections().Single(x => x.ProfileId == b);
        if (!bridge.IsCurrentConnection(original.ConnectionGeneration) || secondChoice.Selected)
            throw new InvalidOperationException("A newly connected profile silently replaced the current profile");

        await writerLock.WaitAsync();
        bool held = true;
        try
        {
            var queued = bridge.ExecuteAsync(original.ConnectionGeneration, "remove", bookmarkId: original.Bar[0].Id);
            if (!bridge.SelectConnection(secondChoice.Generation)) throw new InvalidOperationException("Profile choice was not saved");
            writerLock.Release(); held = false;
            await ExpectChanged(queued);
        }
        finally { if (held) writerLock.Release(); }
        var selected = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (selected.ProfileId != b || !bridge.IsCurrentConnection(selected.ConnectionGeneration))
            throw new InvalidOperationException("Profile selection did not activate its snapshot");
        using var secondReader = new StreamReader(second, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var write = bridge.ExecuteAsync(selected.ConnectionGeneration, "create", parentId: selected.BarId, title: "Only in B");
        string line = await secondReader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("Selected profile closed");
        using (var request = JsonDocument.Parse(line))
        {
            if (request.RootElement.GetProperty("op").GetString() != "create") throw new InvalidOperationException("An old request crossed profiles");
            await WriteLine(second, JsonSerializer.Serialize(new { type = "result", id = request.RootElement.GetProperty("id").GetString(), ok = true }));
        }
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        using (var reloaded = new EdgeFavoritesBridge())
            if (!reloaded.WaitingForSelection) throw new InvalidOperationException("The selected profile was not persisted");

        second.Dispose();
        await WaitUntil(() => !bridge.Connected, "selected profile disconnected");
        if (bridge.IsCurrentConnection(original.ConnectionGeneration)) throw new InvalidOperationException("Disconnected profile silently switched to another profile");
        using var reconnect = await Connect(pipeName);
        await SendSnapshot(reconnect, "Profile B reconnected", profileId: b);
        var reconnected = await snapshots.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (reconnected.ProfileId != b || !bridge.IsCurrentConnection(reconnected.ConnectionGeneration))
            throw new InvalidOperationException("Selected profile did not reconnect");

        using var clone = await Connect(pipeName);
        await SendSnapshot(clone, "Cloned B", profileId: b);
        await WaitUntil(() => !bridge.Connected && bridge.GetConnections().Count(x => x.ProfileId == b && x.Ambiguous) == 2, "duplicate profile identity");
        try { bridge.SelectConnection(reconnected.ConnectionGeneration); throw new InvalidOperationException("Duplicate profile identity was accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("相同的同步标识", StringComparison.Ordinal)) { }
        clone.Dispose();
        await WaitUntil(() => bridge.GetConnections().Count(x => x.ProfileId == b) == 1, "duplicate connection closed");
        if (bridge.Connected) throw new InvalidOperationException("Ambiguous identity reconnected without an explicit selection");
        if (!bridge.SelectConnection(reconnected.ConnectionGeneration)) throw new InvalidOperationException("Explicit profile selection failed");
    }

    private static async Task ExpectChanged(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("连接已变化", StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("A stale bookmark write was not rejected");
    }

    private static async Task SelectProfile(EdgeFavoritesBridge bridge, string name)
    {
        await WaitUntil(() => bridge.GetConnections().Any(choice => choice.Preview == name), "profile choice: " + name);
        var choice = bridge.GetConnections().Single(item => item.Preview == name);
        if (!choice.Selected) bridge.SelectConnection(choice.Generation);
    }

    private static async Task<NamedPipeClientStream> Connect(string pipeName)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        return client;
    }

    private static Task SendSnapshot(Stream stream, string name, int sequence = 1, string? profileId = null) => WriteLine(stream, JsonSerializer.Serialize(new {
        type = "snapshot", sequence, profileId,
        tree = new[] { new { id = "0", children = new[] { new { id = "1", title = "bar", children = new[] { new { id = "42", title = name, url = "https://example.test/" } } } } } }
    }));

    private static async Task WriteLine(Stream stream, string line)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await stream.FlushAsync();
    }

    private static async Task WaitUntil(Func<bool> predicate, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(label); await Task.Delay(30); }
    }
}
