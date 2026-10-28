using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgBrowser.Desktop;

/// <summary>Downloads and verifies public Chrome Web Store CRX3 packages.</summary>
internal static class ChromeStoreExtensions
{
    private const int MaxPackageBytes = 64 * 1024 * 1024;
    private const int MaxHeaderBytes = 256 * 1024;
    // Chromium's production CRX3 publisher key hash. Store packages must have
    // both the developer proof for the requested ID and this publisher proof.
    // Source: https://chromium.googlesource.com/chromium/src/+/refs/heads/main/components/crx_file/crx_verifier.cc
    private static readonly byte[] PublisherHash = Convert.FromHexString("61f7f2a6bfcf74cd0bc1fe2497cc9b04254c658f79f2145392867ea8366367cf");
    private static readonly byte[] SignatureContext = Encoding.ASCII.GetBytes("CRX3 SignedData\0");

    public static string ParseId(string input)
    {
        string value = input.Trim();
        if (value.Length > 4096) throw new ArgumentException("插件网址过长。");
        if (IsId(value)) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("chrome.google.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请输入 Chrome 网上应用店的插件网址或 32 位插件 ID。");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int detail = Array.FindIndex(parts, part => part.Equals("detail", StringComparison.OrdinalIgnoreCase));
        var ids = detail < 0 ? [] : parts.Skip(detail + 1).Where(IsId).ToArray();
        if (ids.Length != 1) throw new ArgumentException("未能从商店网址识别唯一的插件 ID。");
        return ids[0];
    }
    private static bool IsId(string value) => value.Length == 32 && value.All(c => c is >= 'a' and <= 'p');

    public static async Task<ChromeStorePackage> DownloadAsync(string id, string chromeVersion, string proxy, CancellationToken cancellation)
    {
        if (!IsId(id)) throw new ArgumentException("插件 ID 无效。");
        if (!Regex.IsMatch(chromeVersion, @"^\d+(\.\d+){0,3}$")) throw new ArgumentException("无法确定 Chromium 版本。");
        string query = Uri.EscapeDataString($"id={id}&installsource=ondemand&uc");
        var uri = new Uri($"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={Uri.EscapeDataString(chromeVersion)}&acceptformat=crx3&x={query}");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrEmpty(proxy)) handler.Proxy = new WebProxy(BrowserNetworkSettings.NormalizeProxy(proxy));
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                !(uri.Host.Equals("clients2.google.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("clients2.googleusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("插件下载跳转到了非 Google 地址。");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (response.Headers.Location is not { } location || redirects == 5) throw new InvalidDataException("插件下载跳转无效。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPackageBytes) throw new InvalidDataException("插件包超过 64 MiB 限制。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int read = await source.ReadAsync(chunk, cancellation); if (read == 0) break;
                if (buffer.Length + read > MaxPackageBytes) throw new InvalidDataException("插件包超过 64 MiB 限制。");
                buffer.Write(chunk, 0, read);
            }
            return Verify(buffer.ToArray(), id);
        }
        throw new InvalidDataException("插件下载跳转过多。");
    }

    public static ChromeStorePackage Verify(byte[] bytes, string expectedId) => VerifyWithPublisherHash(bytes, expectedId, PublisherHash);
    internal static ChromeStorePackage VerifyWithPublisherHash(byte[] bytes, string expectedId, byte[] publisherHash)
    {
        if (!IsId(expectedId) || publisherHash.Length != 32) throw new InvalidDataException("插件身份无效。");
        if (bytes.Length < 12 || bytes.Length > MaxPackageBytes || !bytes.AsSpan(0, 4).SequenceEqual("Cr24"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 3)
            throw new InvalidDataException("插件不是受支持的 CRX3 格式。");
        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if (headerLength > MaxHeaderBytes || headerLength > bytes.Length - 12) throw new InvalidDataException("插件签名头无效。");
        int archiveOffset = checked(12 + (int)headerLength);
        var header = ProtoFields(bytes.AsMemory(12, (int)headerLength));
        var signedFields = header.Where(field => field.Number == 10000 && field.Wire == 2).ToArray();
        if (signedFields.Length != 1) throw new InvalidDataException("插件签名数据无效。");
        var signedHeader = signedFields[0].Data;
        var idFields = ProtoFields(signedHeader).Where(field => field.Number == 1 && field.Wire == 2).ToArray();
        if (idFields.Length != 1 || idFields[0].Data.Length != 16) throw new InvalidDataException("插件签名 ID 无效。");
        byte[] declaredId = idFields[0].Data.ToArray();
        byte[] expectedBytes = Enumerable.Range(0, 16).Select(i => (byte)(((expectedId[i * 2] - 'a') << 4) | (expectedId[i * 2 + 1] - 'a'))).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(declaredId, expectedBytes)) throw new InvalidDataException("插件包与请求的商店 ID 不一致。");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SignatureContext);
        Span<byte> signedLength = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(signedLength, (uint)signedHeader.Length);
        hash.AppendData(signedLength); hash.AppendData(signedHeader.Span); hash.AppendData(bytes.AsSpan(archiveOffset));
        byte[] digest = hash.GetHashAndReset();
        var proofs = header.Where(field => (field.Number == 2 || field.Number == 3) && field.Wire == 2).ToArray();
        if (proofs.Length is < 1 or > 16) throw new InvalidDataException("插件签名数量无效。");
        byte[]? developerKey = null; bool publisherVerified = false;
        foreach (var proof in proofs)
        {
            var fields = ProtoFields(proof.Data);
            var keys = fields.Where(field => field.Number == 1 && field.Wire == 2).ToArray();
            var signatures = fields.Where(field => field.Number == 2 && field.Wire == 2).ToArray();
            if (keys.Length != 1 || signatures.Length != 1 || keys[0].Data.Length is < 1 or > 16384 || signatures[0].Data.Length is < 1 or > 16384)
                throw new InvalidDataException("插件签名内容无效。");
            var key = keys[0].Data.Span; var signature = signatures[0].Data.Span;
            bool valid;
            try
            {
                if (proof.Number == 2)
                {
                    using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(key, out int consumed);
                    valid = consumed == key.Length && rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
                else
                {
                    using var ecdsa = ECDsa.Create(); ecdsa.ImportSubjectPublicKeyInfo(key, out int consumed);
                    valid = consumed == key.Length && ecdsa.VerifyHash(digest, signature, DSASignatureFormat.Rfc3279DerSequence);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException) { throw new InvalidDataException("插件签名密钥无效。", ex); }
            if (!valid) throw new InvalidDataException("插件签名校验失败。");
            byte[] keyHash = SHA256.HashData(key);
            if (CryptographicOperations.FixedTimeEquals(keyHash.AsSpan(0, 16), declaredId)) developerKey = key.ToArray();
            if (CryptographicOperations.FixedTimeEquals(keyHash, publisherHash)) publisherVerified = true;
        }
        if (developerKey is null || !publisherVerified) throw new InvalidDataException("插件缺少有效的开发者或 Chrome 商店签名。");
        return new ChromeStorePackage(bytes, archiveOffset, expectedId, developerKey);
    }

    private readonly record struct ProtoField(int Number, int Wire, ReadOnlyMemory<byte> Data);
    private static List<ProtoField> ProtoFields(ReadOnlyMemory<byte> data)
    {
        var fields = new List<ProtoField>(); int offset = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (int i = 0; i < 10; i++)
            {
                if (offset >= data.Length) throw new InvalidDataException("插件签名数据被截断。");
                byte part = data.Span[offset++];
                if (i == 9 && (part & 0xfe) != 0) throw new InvalidDataException("插件签名整数无效。");
                value |= (ulong)(part & 0x7f) << (7 * i);
                if ((part & 0x80) == 0) return value;
            }
            throw new InvalidDataException("插件签名整数过长。");
        }
        while (offset < data.Length)
        {
            ulong tag = Varint(); int wire = (int)(tag & 7); ulong number = tag >> 3;
            if (number is 0 or > 536870911 || fields.Count >= 1024) throw new InvalidDataException("插件签名字段无效。");
            ReadOnlyMemory<byte> value = ReadOnlyMemory<byte>.Empty;
            if (wire == 2)
            {
                ulong length = Varint(); if (length > (ulong)(data.Length - offset)) throw new InvalidDataException("插件签名字段被截断。");
                value = data.Slice(offset, (int)length); offset += (int)length;
            }
            else if (wire == 0) _ = Varint();
            else if (wire is 1 or 5)
            {
                int size = wire == 1 ? 8 : 4; if (data.Length - offset < size) throw new InvalidDataException("插件签名字段被截断。"); offset += size;
            }
            else throw new InvalidDataException("插件签名字段类型不受支持。");
            fields.Add(new ProtoField((int)number, wire, value));
        }
        return fields;
    }
}

internal sealed class ChromeStorePackage
{
    private const long MaxExpandedBytes = 256L * 1024 * 1024;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private readonly byte[] bytes;
    private readonly int offset;
    private readonly byte[] developerKey;
    private readonly JsonObject manifest;
    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string[] Permissions { get; }
    public string[] OptionalPermissions { get; }

    public ChromeStorePackage(byte[] bytes, int offset, string id, byte[] developerKey)
    {
        this.bytes = bytes; this.offset = offset; this.developerKey = developerKey; Id = id;
        using var archive = OpenArchive();
        ValidateEntries(archive);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("插件包缺少 manifest.json。");
        manifest = ReadJson(entry) as JsonObject ?? throw new InvalidDataException("插件清单格式无效。");
        int manifestVersion = manifest["manifest_version"]?.GetValue<int>() ?? 0;
        if (manifestVersion is not (2 or 3) || manifest.ContainsKey("theme")) throw new InvalidDataException("只支持 Chromium 插件清单，不支持主题。");
        Version = manifest["version"]?.GetValue<string>() ?? "";
        if (!Regex.IsMatch(Version, @"^\d+(\.\d+){0,3}$") || Version.Length > 32) throw new InvalidDataException("插件版本无效。");
        string rawName = manifest["name"]?.GetValue<string>() ?? "";
        Name = ResolveName(archive, rawName, manifest["default_locale"]?.GetValue<string>());
        if (Name.Length is < 1 or > 160 || Name.Any(char.IsControl)) throw new InvalidDataException("插件名称无效。");
        if (manifest["key"] is not null && manifest["key"] is not JsonValue) throw new InvalidDataException("插件清单中的公钥无效。");
        if (manifest["key"] is JsonValue keyValue)
        {
            byte[] stored;
            try { stored = Convert.FromBase64String(keyValue.GetValue<string>()); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException) { throw new InvalidDataException("插件清单中的公钥无效。", ex); }
            if (!CryptographicOperations.FixedTimeEquals(stored, developerKey)) throw new InvalidDataException("插件清单中的公钥与签名不一致。");
        }
        var required = ReadPermissions(manifest, "permissions", "host_permissions").ToList();
        if (manifest["content_scripts"] is JsonArray scripts)
        {
            if (scripts.Count > 256) throw new InvalidDataException("插件内容脚本清单过大。");
            foreach (var item in scripts)
            {
                if (item is not JsonObject script) throw new InvalidDataException("插件内容脚本清单无效。");
                required.AddRange(ReadPermissions(script, "matches").Select(value => "内容脚本访问：" + value));
            }
        }
        else if (manifest["content_scripts"] is not null) throw new InvalidDataException("插件内容脚本清单无效。");
        Permissions = required.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        OptionalPermissions = ReadPermissions(manifest, "optional_permissions", "optional_host_permissions");
    }
    private ZipArchive OpenArchive() => new(new MemoryStream(bytes, offset, bytes.Length - offset, writable: false), ZipArchiveMode.Read);
    private static JsonNode? ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("插件清单或语言文件过大。");
        using var stream = entry.Open(); using var content = new MemoryStream(); byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length); if (read == 0) break;
            if (content.Length + read > 1024 * 1024 || content.Length + read > entry.Length) throw new InvalidDataException("插件清单或语言文件过大。");
            content.Write(buffer, 0, read);
        }
        if (content.Length != entry.Length) throw new InvalidDataException("插件清单或语言文件被截断。");
        content.Position = 0;
        try { return JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }); }
        catch (JsonException ex) { throw new InvalidDataException("插件清单或语言文件格式无效。", ex); }
    }
    private static string ResolveName(ZipArchive archive, string raw, string? defaultLocale)
    {
        if (!raw.StartsWith("__MSG_", StringComparison.Ordinal) || !raw.EndsWith("__", StringComparison.Ordinal)) return raw.Trim();
        string key = raw[6..^2];
        var locales = new[] { CultureInfo.CurrentUICulture.Name.Replace('-', '_'), CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, defaultLocale, "en" };
        foreach (string locale in locales.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            string path = $"_locales/{locale}/messages.json";
            var entry = archive.Entries.FirstOrDefault(item => item.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (entry is null || ReadJson(entry) is not JsonObject messages) continue;
            var match = messages.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            if (match?["message"] is JsonValue message && message.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return raw;
    }
    private static string[] ReadPermissions(JsonObject manifest, params string[] keys)
    {
        var values = new List<string>();
        foreach (string key in keys)
        {
            if (manifest[key] is null) continue;
            if (manifest[key] is not JsonArray items || items.Count > 256) throw new InvalidDataException("插件权限清单无效。");
            foreach (var item in items)
            {
                string value = item is JsonValue scalar && scalar.TryGetValue<string>(out string? text) ? text : item?.ToJsonString() ?? "";
                if (value.Length is < 1 or > 512 || value.Any(char.IsControl)) throw new InvalidDataException("插件权限条目无效。");
                values.Add(value);
            }
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > 10000) throw new InvalidDataException("插件文件数量无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.Length is < 1 or > 2048 || name.Contains('\\') || name.StartsWith('/') || name.Contains(':')) throw new InvalidDataException("插件包含无效文件路径。");
            string normalized = name.TrimEnd('/');
            var parts = normalized.Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
                throw new InvalidDataException("插件包含无效文件路径。");
            if (!names.Add(normalized)) throw new InvalidDataException("插件包含重复文件路径。");
            int unixMode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixMode == 0xa000 || (entry.ExternalAttributes & 0x400) != 0) throw new InvalidDataException("插件不能包含符号链接。");
            if (entry.Length < 0 || entry.Length > MaxFileBytes || total > MaxExpandedBytes - entry.Length) throw new InvalidDataException("插件解压后的内容过大。");
            total += entry.Length;
        }
    }
    private static bool IsDeviceName(string name)
    {
        string stem = name.Split('.', 2)[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }

    public string ExtractTo(string dataDirectory) => ExtractToCancellable(dataDirectory, CancellationToken.None);
    public string ExtractToCancellable(string dataDirectory, CancellationToken cancellation)
    {
        string directory = Path.Combine(dataDirectory, "store-extensions", Id, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = OpenArchive();
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                string name = entry.FullName;
                if (name.Equals("manifest.json", StringComparison.Ordinal) || name.Equals("_metadata/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_metadata/", StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open(); using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int read = source.Read(buffer, 0, buffer.Length); if (read == 0) break;
                    if (written + read > entry.Length || total + read > MaxExpandedBytes) throw new InvalidDataException("插件解压后的内容超过限制。");
                    destination.Write(buffer, 0, read);
                    written += read; total += read;
                }
                if (written != entry.Length) throw new InvalidDataException("插件文件被截断。");
            }
            cancellation.ThrowIfCancellationRequested();
            var writableManifest = (JsonObject)manifest.DeepClone(); writableManifest["key"] = Convert.ToBase64String(developerKey);
            File.WriteAllText(Path.Combine(directory, "manifest.json"), writableManifest.ToJsonString());
            return directory;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
