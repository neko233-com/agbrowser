using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgBrowser.Desktop;

/// <summary>Bounded local cache of icons observed while opening bookmarked pages.</summary>
internal sealed class FavoritePageIconCache
{
    private const int MaxEntries = 1024, MaxBytes = 2 * 1024 * 1024, MaxFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Entry(byte[] Data, long Updated);
    private readonly string path;
    private readonly object gate = new(), saveGate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool loaded, readable = true, dirty;
    private int bytes;
    private long revision;

    public FavoritePageIconCache(string directory) => path = Path.Combine(directory, "favorite-page-icons.json");

    // Call from a background thread: reading and validating the cache is I/O.
    public IReadOnlyDictionary<string, byte[]> Snapshot()
    {
        lock (gate)
        {
            LoadIfNeeded();
            return entries.ToDictionary(item => item.Key, item => item.Value.Data, StringComparer.Ordinal);
        }
    }

    private void LoadIfNeeded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) throw new JsonException("Icon cache is too large");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("entries").ValueKind != JsonValueKind.Array)
                throw new JsonException("Unknown icon cache format");
            bool migrated = false;
            foreach (var item in root.GetProperty("entries").EnumerateArray())
            {
                string key;
                if (item.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String)
                    key = hash.GetString() ?? "";
                else
                {
                    string legacy = item.GetProperty("url").GetString() ?? "";
                    if (!FavoriteIcons.TryPageKey(legacy, out var normalized) || normalized != legacy) throw new JsonException("Invalid legacy cache key");
                    key = HashPageKey(legacy); migrated = true;
                }
                string encoded = item.GetProperty("icon").GetString() ?? "";
                long updated = item.GetProperty("updated").GetInt64();
                if (!ValidHash(key) || encoded.Length > 180_000 || updated <= 0)
                    throw new JsonException("Invalid icon cache entry");
                byte[] data = Convert.FromBase64String(encoded);
                if (!ValidPng(data) || entries.Count >= MaxEntries || bytes + data.Length > MaxBytes || !entries.TryAdd(key, new Entry(data, updated)))
                    throw new JsonException("Invalid icon cache content");
                bytes += data.Length;
            }
            if (migrated) { revision++; dirty = true; }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { entries.Clear(); bytes = 0; readable = false; dirty = false; }
    }

    // Data must already have been normalized to a small PNG by WebView2.
    public bool Record(string key, byte[] data)
    {
        if (!FavoriteIcons.TryPageKey(key, out var normalized) || normalized != key || !ValidPng(data)) return false;
        key = HashPageKey(key);
        lock (gate)
        {
            // Loading is performed in the icon worker before the UI index is published.
            if (!loaded) return false;
            if (entries.TryGetValue(key, out var old) && old.Data.AsSpan().SequenceEqual(data)) return false;
            if (old is not null) bytes -= old.Data.Length;
            entries[key] = new Entry(data, DateTime.UtcNow.Ticks); bytes += data.Length;
            while (entries.Count > MaxEntries || bytes > MaxBytes)
            {
                var oldest = entries.MinBy(item => item.Value.Updated);
                entries.Remove(oldest.Key); bytes -= oldest.Value.Data.Length;
            }
            revision++; dirty = readable;
            return readable;
        }
    }

    public void Save()
    {
        lock (saveGate)
        {
            KeyValuePair<string, Entry>[] copy; long captured;
            lock (gate)
            {
                if (!loaded || !readable || !dirty) return;
                copy = entries.ToArray(); captured = revision;
            }
            string temporary = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new { version = 1, entries = copy.Select(item => new { hash = item.Key, icon = Convert.ToBase64String(item.Value.Data), updated = item.Value.Updated }) }, JsonOptions);
                    if (stream.Length > MaxFileBytes) throw new IOException("Icon cache exceeds its file limit");
                }
                File.Move(temporary, path, true);
                lock (gate) if (revision == captured) dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }

    internal static string HashPageKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private static bool ValidHash(string key) => key.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidPng(byte[] data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 24 || data.Length > 131072 || !data.AsSpan(0, 8).SequenceEqual(signature) || !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        return width is > 0 and <= 64 && height is > 0 and <= 64;
    }
}
