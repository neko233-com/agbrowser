using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgBrowser.Desktop;

/// <summary>Local, current-user-only channel to the Edge native-messaging host.</summary>
internal sealed class EdgeFavoritesBridge : IDisposable
{
    public const string ExtensionId = "fjkcppekpigobilckiggnjdfeiiabmee";
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim writerLock = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<long, BridgeConnection> connections = new();
    private readonly ConcurrentDictionary<string, PendingRequest> pending = new();
    private sealed class BridgeConnection(NamedPipeServerStream pipe, long generation)
    {
        public NamedPipeServerStream Pipe { get; } = pipe;
        public long Generation { get; } = generation;
        public long LastSequence { get; set; }
        public FavoriteSnapshot? Snapshot { get; set; }
    }
    private sealed record PendingRequest(BridgeConnection Connection, TaskCompletionSource<JsonElement> Completion);
    internal sealed record ConnectionChoice(long Generation, string? ProfileId, string Preview, bool Selected, bool Ambiguous);
    private BridgeConnection? connection;
    private Task? listenerTask;
    private int started;
    private long nextGeneration;
    private string? selectedProfileId;
    private bool requireManualSelection;
    private bool selectionFileReadable = true;
    private bool selectionSaved;
    private readonly string selectionPath = Path.Combine(BrowserWindow.ResolveDataDir(), "edge-favorites-profile.json");

    public EdgeFavoritesBridge() => LoadSelection();

    public bool Connected => Volatile.Read(ref connection)?.Pipe.IsConnected == true;
    public long CurrentGeneration => Volatile.Read(ref connection)?.Generation ?? 0;
    public bool WaitingForSelection { get { lock (gate) return connection is null && (requireManualSelection || selectedProfileId is not null); } }
    public bool SelectionSaved { get { lock (gate) return selectionSaved; } }
    public bool IsCurrentConnection(long generation)
    {
        var current = Volatile.Read(ref connection);
        return generation != 0 && current?.Generation == generation && current.Pipe.IsConnected;
    }
    public event Action<FavoriteSnapshot>? SnapshotReceived;
    public event Action<bool>? ConnectionChanged;
    public event Action? ProfilesChanged;
    private static string PipeName => "agbrowser-edge-favorites-" + SingleInstance.ProfileHash(BrowserWindow.ResolveDataDir());

    private void LoadSelection()
    {
        try
        {
            using var file = new FileStream(selectionPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) throw new JsonException("Edge 收藏配置文件过大");
            using var doc = JsonDocument.Parse(file);
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) throw new JsonException("不支持的 Edge 收藏配置版本");
            if (root.TryGetProperty("profileId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParseExact(id.GetString(), "D", out var parsed)) throw new JsonException("无效的 Edge 收藏配置标识");
                selectedProfileId = parsed.ToString("D");
            }
            requireManualSelection = root.TryGetProperty("requireManualSelection", out var manual) && manual.GetBoolean();
            if (selectedProfileId is null && !requireManualSelection) throw new JsonException("Edge 收藏配置不完整");
            selectionSaved = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { selectionFileReadable = false; requireManualSelection = true; }
    }

    private bool SaveSelection(string? profileId)
    {
        if (!selectionFileReadable) return false;
        string temporary = selectionPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(selectionPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = 1, profileId, requireManualSelection = profileId is null }));
            File.Move(temporary, selectionPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    public IReadOnlyList<ConnectionChoice> GetConnections()
    {
        lock (gate)
        {
            return connections.Values.Where(c => c.Snapshot is not null && c.Pipe.IsConnected).OrderBy(c => c.Generation).Select(c =>
            {
                var snapshot = c.Snapshot!;
                string preview = string.Join("、", snapshot.Bar.Take(3).Select(n => n.Name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ')).Where(n => !string.IsNullOrWhiteSpace(n)));
                if (preview.Length > 60) preview = preview[..60] + "…";
                if (preview.Length == 0) preview = "收藏栏为空";
                bool duplicate = snapshot.ProfileId is { } id && connections.Values.Count(other => other.Snapshot?.ProfileId == id && other.Pipe.IsConnected) > 1;
                return new ConnectionChoice(c.Generation, snapshot.ProfileId, preview, ReferenceEquals(connection, c), duplicate);
            }).ToArray();
        }
    }

    // Persist only a stable profile-local ID. Older extensions remain usable,
    // but their selection requires an explicit choice again after restart.
    public bool SelectConnection(long generation)
    {
        FavoriteSnapshot snapshot;
        bool persisted;
        lock (gate)
        {
            if (!connections.TryGetValue(generation, out var chosen) || !chosen.Pipe.IsConnected || chosen.Snapshot is not { } current)
                throw new InvalidOperationException("Edge 收藏连接已变化，请重新选择。");
            if (current.ProfileId is { } id && connections.Values.Count(c => c.Snapshot?.ProfileId == id && c.Pipe.IsConnected) > 1)
                throw new InvalidOperationException("两个 Edge 配置使用了相同的同步标识。请暂时停用其中一个配置的扩展，再选择要使用的配置。");
            persisted = SaveSelection(current.ProfileId);
            selectionSaved = persisted;
            selectedProfileId = current.ProfileId;
            requireManualSelection = current.ProfileId is null;
            Volatile.Write(ref connection, chosen);
            snapshot = current;
        }
        ConnectionChanged?.Invoke(true);
        SnapshotReceived?.Invoke(snapshot);
        ProfilesChanged?.Invoke();
        return persisted;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) Volatile.Write(ref listenerTask, Task.Run(ListenAsync));
    }
    internal Task WaitForStopAsync() => Volatile.Read(ref listenerTask) ?? Task.CompletedTask;
    private async Task ListenAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token);
                var accepted = new BridgeConnection(server, Interlocked.Increment(ref nextGeneration));
                bool selected = false, admitted;
                lock (gate)
                {
                    admitted = connections.Count < 16 && !stop.IsCancellationRequested;
                    if (admitted)
                    {
                        connections.Add(accepted.Generation, accepted);
                        if (connection is null && selectedProfileId is null && !requireManualSelection)
                        { Volatile.Write(ref connection, accepted); selected = true; }
                    }
                }
                if (!admitted) { server.Dispose(); server = null; continue; }
                server = null; // HandleConnection owns the accepted stream.
                if (selected) ConnectionChanged?.Invoke(true);
                _ = Task.Run(() => HandleConnection(accepted));
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleConnection(BridgeConnection accepted)
    {
        try
        {
            using var reader = new StreamReader(accepted.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!stop.IsCancellationRequested && accepted.Pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(stop.Token);
                if (line is null || line.Length > 16 * 1024 * 1024) break;
                try { Receive(line, accepted); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            bool changed = false;
            FavoriteSnapshot? replacement = null;
            lock (gate)
            {
                connections.Remove(accepted.Generation);
                if (ReferenceEquals(connection, accepted))
                {
                    // An older extension has no stable profile identity. Once
                    // it disconnects, another connection cannot safely be
                    // assumed to belong to the same Edge profile.
                    if (accepted.Snapshot is { ProfileId: null } && selectedProfileId is null && !requireManualSelection)
                    { requireManualSelection = true; selectionSaved = SaveSelection(null); }
                    Volatile.Write(ref connection, null); changed = true;
                    var next = FindAutomaticSelection();
                    if (next is not null) { Volatile.Write(ref connection, next); replacement = next.Snapshot; }
                }
            }
            accepted.Pipe.Dispose();
            foreach (var item in pending.ToArray())
                if (ReferenceEquals(item.Value.Connection, accepted) && pending.TryRemove(item.Key, out var wait))
                    wait.Completion.TrySetException(new IOException("Edge 收藏同步连接已断开"));
            if (changed) ConnectionChanged?.Invoke(Connected);
            if (replacement is not null) SnapshotReceived?.Invoke(replacement);
            ProfilesChanged?.Invoke();
        }
    }

    // Called under gate. Never silently switch a chosen profile to another one.
    private BridgeConnection? FindAutomaticSelection()
    {
        if (requireManualSelection) return null;
        if (selectedProfileId is null) return connections.Values.Where(c => c.Pipe.IsConnected).OrderBy(c => c.Generation).FirstOrDefault();
        var matching = connections.Values.Where(c => c.Pipe.IsConnected && c.Snapshot?.ProfileId == selectedProfileId).Take(2).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    private void Receive(string line, BridgeConnection source)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        string type = root.GetProperty("type").GetString() ?? "";
        if (type == "result")
        {
            string id = root.GetProperty("id").GetString() ?? "";
            if (pending.TryGetValue(id, out var request) && ReferenceEquals(request.Connection, source) && pending.TryRemove(id, out var wait))
            {
                if (root.GetProperty("ok").GetBoolean()) wait.Completion.TrySetResult(root.Clone());
                else wait.Completion.TrySetException(new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "Edge 收藏操作失败"));
            }
        }
        else if (type == "snapshot")
        {
            long sequence = root.GetProperty("sequence").GetInt64();
            var tree = root.GetProperty("tree");
            string? profileId = root.TryGetProperty("profileId", out var identity) && identity.ValueKind == JsonValueKind.String && Guid.TryParseExact(identity.GetString(), "D", out var parsed) ? parsed.ToString("D") : null;
            var snapshot = EdgeFavoritesReader.ReadApiSnapshot(tree, source.Generation, profileId);
            if (snapshot is null) return;
            bool activeChanged = false, deliver = false, changed = false;
            lock (gate)
            {
                if (!connections.ContainsKey(source.Generation) || sequence <= source.LastSequence) return;
                if (source.Snapshot is { } existing && existing.ProfileId != profileId) return;
                source.LastSequence = sequence;
                changed = source.Snapshot is not { } previous || !SameFavorites(previous, snapshot);
                if (!changed) return;
                source.Snapshot = snapshot;
                if (ReferenceEquals(connection, source) && selectedProfileId is null && !requireManualSelection && profileId is not null)
                { selectedProfileId = profileId; selectionSaved = SaveSelection(profileId); }
                if (profileId is not null && connections.Values.Count(c => c.Pipe.IsConnected && c.Snapshot?.ProfileId == profileId) > 1 &&
                    connection?.Snapshot?.ProfileId == profileId)
                {
                    // Cloned extension storage cannot be distinguished safely.
                    Volatile.Write(ref connection, null); selectedProfileId = null; requireManualSelection = true;
                    selectionSaved = SaveSelection(null); activeChanged = true;
                }
                if (connection is null)
                {
                    var next = FindAutomaticSelection();
                    if (next is not null) { Volatile.Write(ref connection, next); activeChanged = true; }
                }
                deliver = ReferenceEquals(connection, source);
            }
            if (activeChanged) ConnectionChanged?.Invoke(Connected);
            if (deliver) SnapshotReceived?.Invoke(snapshot);
            ProfilesChanged?.Invoke();
        }
    }

    private static bool SameFavorites(FavoriteSnapshot left, FavoriteSnapshot right) =>
        left.BarId == right.BarId && SameNodes(left.Bar, right.Bar) && SameNode(left.Other, right.Other) && SameNodes(left.AdditionalRoots ?? [], right.AdditionalRoots ?? []);

    private static bool SameNode(FavoriteNode? left, FavoriteNode? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.Id == right.Id && left.Name == right.Name && left.Url == right.Url && left.CanModify == right.CanModify && SameNodes(left.Children, right.Children));

    private static bool SameNodes(IReadOnlyList<FavoriteNode> left, IReadOnlyList<FavoriteNode> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++) if (!SameNode(left[i], right[i])) return false;
        return true;
    }

    public async Task ExecuteAsync(long expectedGeneration, string op, string? parentId = null, string? bookmarkId = null, string? title = null, string? url = null)
    {
        var current = Volatile.Read(ref connection);
        if (current is null || current.Generation != expectedGeneration || !current.Pipe.IsConnected)
            throw new InvalidOperationException("Edge 收藏连接已变化，请重新选择收藏后再试。");
        string id = Guid.NewGuid().ToString("N");
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool mayHaveBeenSent = false;
        try
        {
            string json = JsonSerializer.Serialize(new { id, op, parentId, bookmarkId, title, url }) + "\n";
            await writerLock.WaitAsync(stop.Token);
            try
            {
                if (!ReferenceEquals(Volatile.Read(ref connection), current) || !current.Pipe.IsConnected)
                    throw new InvalidOperationException("Edge 收藏连接已变化，请重新选择收藏后再试。");
                pending[id] = new PendingRequest(current, result);
                byte[] bytes = Encoding.UTF8.GetBytes(json); mayHaveBeenSent = true; await current.Pipe.WriteAsync(bytes, stop.Token); await current.Pipe.FlushAsync(stop.Token);
            }
            finally { writerLock.Release(); }
            await result.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
        }
        catch (Exception ex) when (mayHaveBeenSent && (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException))
        { throw new InvalidOperationException("连接中断或响应超时，操作可能已在 Edge 完成。请先检查收藏，再决定是否重试。", ex); }
        finally { pending.TryRemove(id, out _); }
    }

    public void Dispose()
    {
        stop.Cancel();
        lock (gate) foreach (var source in connections.Values) source.Pipe.Dispose();
    }

    /// <summary>Edge starts this same app with its extension origin as argv[0].</summary>
    public static async Task<int> RunNativeHostAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(1500);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            using var stop = new CancellationTokenSource();
            var inbound = CopyFromEdge(input, client, stop.Token);
            var outbound = CopyToEdge(client, output, stop.Token);
            var finished = await Task.WhenAny(inbound, outbound);
            stop.Cancel();
            await finished;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
        { Console.Error.WriteLine($"agbrowser edge bridge: {ex.GetType().Name}: {ex.Message}"); return 1; }
    }
    private static async Task CopyFromEdge(Stream input, Stream pipe, CancellationToken stop)
    {
        byte[] header = new byte[4];
        while (!stop.IsCancellationRequested)
        {
            await input.ReadExactlyAsync(header, stop);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 1 || length > 16 * 1024 * 1024) throw new IOException("Invalid native message length");
            byte[] bytes = new byte[length]; await input.ReadExactlyAsync(bytes, stop);
            using var json = JsonDocument.Parse(bytes);
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json.RootElement) + "\n");
            await pipe.WriteAsync(line, stop); await pipe.FlushAsync(stop);
        }
    }
    private static async Task CopyToEdge(Stream pipe, Stream output, CancellationToken stop)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        while (!stop.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(stop); if (line is null) break;
            byte[] bytes = Encoding.UTF8.GetBytes(line); if (bytes.Length > 1024 * 1024) throw new IOException("Native message too large");
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await output.WriteAsync(header, stop); await output.WriteAsync(bytes, stop); await output.FlushAsync(stop);
        }
    }
}
