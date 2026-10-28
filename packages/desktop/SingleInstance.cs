using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgBrowser.Desktop;

/// <summary>Routes subsequent shell launches to the existing window for the same profile.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly string pipeName;
    public bool IsOwner { get; }

    public SingleInstance(string dataDir)
    {
        string hash = ProfileHash(dataDir);
        pipeName = $"agbrowser-{hash}";
        mutex = new Mutex(true, $"Local\\{pipeName}", out bool created);
        IsOwner = created;
    }

    internal static string ProfileHash(string dataDir)
    {
        string path = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
    }

    public async Task ForwardAsync(string[] arguments)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(arguments));
    }

    public void Start(Action<string[]> dispatch) => _ = ListenAsync(dispatch);

    private async Task ListenAsync(Action<string[]> dispatch)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(stop.Token);
                if (line is { Length: <= 65536 } && JsonSerializer.Deserialize<string[]>(line) is { } args)
                    dispatch(args);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* Ignore a disconnected client and accept the next one. */ }
            catch (JsonException) { /* Ignore malformed messages. */ }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        if (IsOwner) mutex.ReleaseMutex();
        mutex.Dispose();
        stop.Dispose();
    }
}
