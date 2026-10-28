using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgBrowser.Desktop;

internal static class ChromeStoreTests
{
    private static readonly Type Service = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.ChromeStoreExtensions")!;
    private static object Invoke(string name, params object[] arguments)
    {
        try { return Service.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, arguments)!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception("FAIL " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { return; }
        throw new Exception("FAIL " + message);
    }
    private static byte[] Varint(ulong value)
    {
        using var stream = new MemoryStream();
        do { byte part = (byte)(value & 0x7f); value >>= 7; stream.WriteByte((byte)(part | (value == 0 ? 0 : 0x80))); } while (value != 0);
        return stream.ToArray();
    }
    private static byte[] Field(int number, byte[] value) => [.. Varint((ulong)(number * 8 + 2)), .. Varint((ulong)value.Length), .. value];
    private static byte[] Archive(params string[] extraEntries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string value, bool symlink = false)
            {
                var entry = archive.CreateEntry(name);
                if (symlink) entry.ExternalAttributes = unchecked((int)0xa0000000);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(value);
            }
            Add("manifest.json", "{\"manifest_version\":3,\"name\":\"Fixture plugin\",\"version\":\"1.2\",\"permissions\":[\"storage\"],\"host_permissions\":[\"https://example.test/*\"],\"content_scripts\":[{\"matches\":[\"<all_urls>\"],\"js\":[\"content.js\"]}]}");
            Add("content.js", "document.body.dataset.agbFixture='loaded';");
            foreach (string name in extraEntries) Add(name == "symlink" ? "link" : name, "fixture", name == "symlink");
        }
        return stream.ToArray();
    }
    private static (byte[] Package, string Id, byte[] PublisherHash, byte[] DeveloperKey) SignedPackage(byte[] archive)
    {
        using var developer = RSA.Create(2048); using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] developerKey = developer.ExportSubjectPublicKeyInfo(), publisherKey = publisher.ExportSubjectPublicKeyInfo();
        byte[] id = SHA256.HashData(developerKey)[..16];
        string textId = string.Concat(id.Select(value => $"{(char)('a' + (value >> 4))}{(char)('a' + (value & 15))}"));
        byte[] signedHeader = Field(1, id); byte[] size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)signedHeader.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("CRX3 SignedData\0")); hash.AppendData(size); hash.AppendData(signedHeader); hash.AppendData(archive);
        byte[] digest = hash.GetHashAndReset();
        byte[] developerProof = [.. Field(1, developerKey), .. Field(2, developer.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))];
        byte[] publisherProof = [.. Field(1, publisherKey), .. Field(2, publisher.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence))];
        byte[] header = [.. Field(2, developerProof), .. Field(3, publisherProof), .. Field(10000, signedHeader)];
        byte[] prefix = new byte[12]; Encoding.ASCII.GetBytes("Cr24").CopyTo(prefix, 0); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), 3); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(8), (uint)header.Length);
        return ([.. prefix, .. header, .. archive], textId, SHA256.HashData(publisherKey), developerKey);
    }
    private static object Verify((byte[] Package, string Id, byte[] PublisherHash, byte[] DeveloperKey) item) => Invoke("VerifyWithPublisherHash", item.Package, item.Id, item.PublisherHash);

    internal static ChromeStorePackage CreateSignedFixture()
    {
        var fixture = SignedPackage(Archive());
        return ChromeStoreExtensions.VerifyWithPublisherHash(fixture.Package, fixture.Id, fixture.PublisherHash);
    }

    public static void Run(string root)
    {
        const string known = "aapbdbdomjkkjkaonfhkkikfgjllcleb";
        Assert((string)Invoke("ParseId", known) == known, "plain store ID accepted");
        Assert((string)Invoke("ParseId", "https://chromewebstore.google.com/detail/example/" + known + "?hl=zh-CN") == known, "store URL accepted");
        Reject(() => Invoke("ParseId", "https://chromewebstore.google.com.evil.test/detail/" + known), "untrusted store host rejected");
        Reject(() => Invoke("ParseId", "http://chromewebstore.google.com/detail/" + known), "insecure store URL rejected");
        var signed = SignedPackage(Archive());
        object package = Verify(signed); Type type = package.GetType();
        Assert((string)type.GetProperty("Name")!.GetValue(package)! == "Fixture plugin", "verified manifest is read");
        Assert(((string[])type.GetProperty("Permissions")!.GetValue(package)!).Contains("https://example.test/*"), "requested host permission shown");
        Assert(((string[])type.GetProperty("Permissions")!.GetValue(package)!).Contains("内容脚本访问：<all_urls>"), "content-script host access shown");
        string folder = (string)type.GetMethod("ExtractTo")!.Invoke(package, [root])!;
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
        Assert(Convert.FromBase64String((string)manifest["key"]!).SequenceEqual(signed.DeveloperKey), "extracted extension preserves signed identity");
        Assert(File.Exists(Path.Combine(folder, "content.js")), "verified content extracted");
        string parent = Path.GetDirectoryName(folder)!; int existing = Directory.GetDirectories(parent).Length;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { type.GetMethod("ExtractToCancellable")!.Invoke(package, [root, cancellation.Token]); throw new Exception("FAIL cancelled extraction unexpectedly completed"); }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
        }
        Assert(Directory.GetDirectories(parent).Length == existing && File.Exists(Path.Combine(folder, "content.js")), "cancelled extraction removes only its own partial directory");
        byte[] tampered = (byte[])signed.Package.Clone(); tampered[^1] ^= 1;
        Reject(() => Invoke("VerifyWithPublisherHash", tampered, signed.Id, signed.PublisherHash), "tampered archive rejected");
        Reject(() => Invoke("VerifyWithPublisherHash", signed.Package, known, signed.PublisherHash), "wrong extension ID rejected");
        Reject(() => Invoke("VerifyWithPublisherHash", signed.Package, signed.Id, new byte[32]), "unknown publisher rejected");
        var traversal = SignedPackage(Archive("../escape.txt"));
        Reject(() => Verify(traversal), "signed path traversal rejected");
        var duplicate = SignedPackage(Archive("MANIFEST.JSON"));
        Reject(() => Verify(duplicate), "case-insensitive duplicate rejected");
        var device = SignedPackage(Archive("NUL.txt"));
        Reject(() => Verify(device), "Windows device path rejected");
        var symlink = SignedPackage(Archive("symlink"));
        Reject(() => Verify(symlink), "archive symlink rejected");
        Console.WriteLine("PASS Chrome Store signatures, publisher and ID checks, manifest identity, and unsafe archive rejection");
    }
}
