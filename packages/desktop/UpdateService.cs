using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgBrowser.Desktop;

internal sealed record UpdateNetworkSettings(string? ProxyUrl = null, string? MirrorUrl = null);
internal sealed record UpdatePackage(Version Version, string FileName, Uri DownloadUrl, byte[] Sha256, long Size);
internal sealed record UpdateCheckResult(Version Current, Version Latest, UpdatePackage? Package);

/// <summary>Checks official release metadata and verifies installers before use.</summary>
internal sealed class UpdateService
{
    private const string ReleaseApi = "https://api.github.com/repos/neko233-com/agbrowser/releases/latest";
    private const string ReleasePage = "https://github.com/neko233-com/agbrowser/releases/latest";
    private const string DefaultMirror = "https://gh.llkk.cc/";
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private readonly string settingsPath, downloadDirectory;
    private readonly Func<UpdateNetworkSettings, HttpClient> clientFactory;
    private bool settingsReadable = true;
    public UpdateNetworkSettings Network { get; private set; }
    public Version Current { get; }

    public UpdateService(string dataDir, Version? current = null, Func<UpdateNetworkSettings, HttpClient>? createClient = null)
    {
        settingsPath = Path.Combine(dataDir, "update-network.json");
        downloadDirectory = Path.Combine(dataDir, "updates");
        Current = NormalizeVersion(current ?? typeof(BrowserWindow).Assembly.GetName().Version ?? new Version(0, 0, 0));
        clientFactory = createClient ?? CreateClient;
        try
        {
            using var stream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Network = ValidateNetwork(JsonSerializer.Deserialize<UpdateNetworkSettings>(stream) ?? new());
        }
        catch (FileNotFoundException) { Network = new(); }
        catch (DirectoryNotFoundException) { Network = new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { settingsReadable = false; Network = new(); }
    }

    public void SaveNetwork(string proxy, string mirror)
    {
        if (!settingsReadable) throw new IOException("现有更新网络设置无法读取，未覆盖原文件。");
        var updated = ValidateNetwork(new(proxy, mirror));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, settingsPath, true);
        Network = updated;
    }

    private static UpdateNetworkSettings ValidateNetwork(UpdateNetworkSettings settings)
    {
        string? proxy = string.IsNullOrWhiteSpace(settings.ProxyUrl) ? null : settings.ProxyUrl.Trim();
        string? mirror = string.IsNullOrWhiteSpace(settings.MirrorUrl) ? null : settings.MirrorUrl.Trim();
        if (proxy != null && (!Uri.TryCreate(proxy, UriKind.Absolute, out var p) ||
            p.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5") ||
            string.IsNullOrEmpty(p.Host) || !string.IsNullOrEmpty(p.UserInfo) || p.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(p.Query) || !string.IsNullOrEmpty(p.Fragment)))
            throw new ArgumentException("代理地址格式无效：请使用不含账号密码的 http、https 或 socks 地址。");
        if (mirror != null && (!Uri.TryCreate(mirror, UriKind.Absolute, out var m) ||
            m.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(m.Host) || !string.IsNullOrEmpty(m.UserInfo) ||
            !string.IsNullOrEmpty(m.Query) || !string.IsNullOrEmpty(m.Fragment)))
            throw new ArgumentException("下载镜像必须是没有账号密码、查询参数或片段的 HTTPS 地址。");
        return new(proxy, mirror is null ? null : mirror.TrimEnd('/') + "/");
    }

    private static HttpClient CreateClient(UpdateNetworkSettings settings)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 };
        if (settings.ProxyUrl is { } proxy) handler.Proxy = new WebProxy(new Uri(proxy));
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("agbrowser-update/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation)
    {
        using var client = clientFactory(Network);
        try { return await CheckApiAsync(client, cancellation); }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return await CheckReleasePageAsync(client, cancellation);
    }

    private async Task<UpdateCheckResult> CheckApiAsync(HttpClient client, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.AbsoluteUri != ReleaseApi)
            throw new InvalidDataException("更新信息没有来自 GitHub 官方 API。");
        if (response.Content.Headers.ContentLength is > MaxMetadataBytes) throw new InvalidDataException("GitHub 更新信息过大。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > MaxMetadataBytes) throw new InvalidDataException("GitHub 更新信息过大。");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var doc = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token);
        return ParseRelease(doc.RootElement, Current);
    }

    private async Task<UpdateCheckResult> CheckReleasePageAsync(HttpClient client, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasePage);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var destination = response.RequestMessage?.RequestUri;
        const string tagPrefix = "/neko233-com/agbrowser/releases/tag/";
        if (destination is null || destination.Scheme != Uri.UriSchemeHttps || destination.Host != "github.com" ||
            !destination.AbsolutePath.StartsWith(tagPrefix, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(destination.Query) || !string.IsNullOrEmpty(destination.Fragment))
            throw new InvalidDataException("最新版本页面没有来自 agbrowser 的 GitHub Release。");
        string tag = Uri.UnescapeDataString(destination.AbsolutePath[tagPrefix.Length..]);
        var latest = ParseTag(tag, out _);
        if (latest <= Current) return new(Current, latest, null);
        string expandedUrl = "https://github.com/neko233-com/agbrowser/releases/expanded_assets/" + Uri.EscapeDataString(tag);
        using var assetsRequest = new HttpRequestMessage(HttpMethod.Get, expandedUrl);
        assetsRequest.Headers.Accept.ParseAdd("text/html");
        using var assets = await client.SendAsync(assetsRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        assets.EnsureSuccessStatusCode();
        if (assets.RequestMessage?.RequestUri?.AbsoluteUri != expandedUrl)
            throw new InvalidDataException("安装包列表没有来自 GitHub 官方页面。");
        if (assets.Content.Headers.ContentLength is > MaxMetadataBytes) throw new InvalidDataException("GitHub 更新信息过大。");
        await using var source = await assets.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16384]; int count;
        while ((count = await source.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + count > MaxMetadataBytes) throw new InvalidDataException("GitHub 更新信息过大。");
            buffer.Write(chunk, 0, count);
        }
        return ParseExpandedAssets(Encoding.UTF8.GetString(buffer.ToArray()), tag, Current);
    }

    internal static UpdateCheckResult ParseRelease(JsonElement root, Version current)
    {
        current = NormalizeVersion(current);
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub 最新版本不是正式版。");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = ParseTag(tag, out string versionText);
        if (latest <= current) return new(current, latest, null);
        string name = $"agbrowser-{versionText}-x64.msi";
        JsonElement found = default;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
            if (asset.GetProperty("name").GetString() == name) { found = asset; break; }
        if (found.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"GitHub {tag} 没有 Windows x64 桌面安装包。");
        string url = found.GetProperty("browser_download_url").GetString() ?? "";
        string expected = $"https://github.com/neko233-com/agbrowser/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var download) || download.AbsoluteUri != expected)
            throw new InvalidDataException("安装包地址不属于 agbrowser 的 GitHub Release。");
        string digest = found.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            throw new InvalidDataException("该 Release 缺少可信的 SHA-256 摘要，不能自动安装。");
        byte[] bytes;
        try { bytes = Convert.FromHexString(digest[7..]); }
        catch (FormatException) { throw new InvalidDataException("Release 的 SHA-256 摘要格式无效。"); }
        long size = found.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidDataException("安装包大小无效。");
        return new(current, latest, new(latest, name, download, bytes, size));
    }

    internal static UpdateCheckResult ParseExpandedAssets(string html, string tag, Version current)
    {
        current = NormalizeVersion(current);
        var latest = ParseTag(tag, out string versionText);
        if (latest <= current) return new(current, latest, null);
        string name = $"agbrowser-{versionText}-x64.msi";
        string relative = $"/neko233-com/agbrowser/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        string anchor = "href=\"" + relative + "\"";
        int position = html.IndexOf(anchor, StringComparison.Ordinal);
        if (position < 0) throw new InvalidDataException($"GitHub {tag} 没有 Windows x64 桌面安装包。");
        if (html.IndexOf(anchor, position + anchor.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("GitHub 安装包信息不唯一。");
        int start = html.LastIndexOf("<li", position, StringComparison.OrdinalIgnoreCase);
        int end = html.IndexOf("</li>", position, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0) throw new InvalidDataException("无法读取 GitHub 安装包信息。");
        string item = html[start..end];
        var hashes = Regex.Matches(item, "sha256:([a-fA-F0-9]{64})", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (hashes.Length != 1) throw new InvalidDataException("该 Release 缺少可信的 SHA-256 摘要，不能自动安装。");
        return new(current, latest, new(latest, name, new Uri("https://github.com" + relative), Convert.FromHexString(hashes[0]), 0));
    }

    private static Version ParseTag(string tag, out string versionText)
    {
        versionText = tag.StartsWith('v') ? tag[1..] : tag;
        string[] parts = versionText.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Any(c => c is < '0' or > '9')) ||
            !Version.TryParse(versionText, out var version)) throw new InvalidDataException("GitHub 版本号格式不受支持。");
        return NormalizeVersion(version);
    }

    public async Task<string> DownloadAsync(UpdatePackage package, CancellationToken cancellation)
    {
        Directory.CreateDirectory(downloadDirectory);
        string target = Path.Combine(downloadDirectory, package.FileName);
        if (await MatchesAsync(target, package, cancellation)) return target;
        using var client = clientFactory(Network);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { return await DownloadOneAsync(client, package.DownloadUrl, target, package, timeout.Token); }
        catch (Exception ex) when (!cancellation.IsCancellationRequested && (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException))
        {
            // The release metadata and digest always come from api.github.com.
            // A mirror can only supply bytes that match that digest.
            string prefix = Network.MirrorUrl ?? DefaultMirror;
            var mirror = new Uri(prefix + package.DownloadUrl.AbsoluteUri);
            return await DownloadOneAsync(client, mirror, target, package, timeout.Token);
        }
    }

    private static async Task<bool> MatchesAsync(string file, UpdatePackage package, CancellationToken cancellation)
    {
        if (!File.Exists(file)) return false;
        long size = new FileInfo(file).Length;
        if (size <= 0 || size > MaxInstallerBytes || (package.Size > 0 && size != package.Size)) return false;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellation);
        return CryptographicOperations.FixedTimeEquals(hash, package.Sha256);
    }

    private static async Task<string> DownloadOneAsync(HttpClient client, Uri url, string target, UpdatePackage package, CancellationToken cancellation)
    {
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length &&
                (length <= 0 || length > MaxInstallerBytes || (package.Size > 0 && length != package.Size)))
                throw new InvalidDataException("安装包大小与 GitHub 不一致。");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buffer = new byte[65536]; long total = 0; int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += count;
                    if (total > MaxInstallerBytes || (package.Size > 0 && total > package.Size)) throw new InvalidDataException("安装包大小超过限制。");
                    digest.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (total == 0 || (package.Size > 0 && total != package.Size) || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), package.Sha256))
                    throw new InvalidDataException("安装包 SHA-256 校验失败，未安装。");
            }
            File.Move(temporary, target, true);
            return target;
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static Version NormalizeVersion(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), 0);
}
