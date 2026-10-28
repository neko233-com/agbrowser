using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using AgBrowser.Desktop;

internal static class Program
{
    private static readonly object OutputLock = new();
    private static long latestGeneration;
    private static void Output(object value) { lock (OutputLock) Console.WriteLine(JsonSerializer.Serialize(value)); }
    private static void Snapshot(object value)
    {
        Interlocked.Exchange(ref latestGeneration, (long)value.GetType().GetProperty("ConnectionGeneration")!.GetValue(value)!);
        Output(new { type = "snapshot", data = value });
    }

    private static async Task<int> Main()
    {
        var bridgeType = typeof(BrowserWindow).Assembly.GetType("AgBrowser.Desktop.EdgeFavoritesBridge")!;
        using var bridge = (IDisposable)Activator.CreateInstance(bridgeType, nonPublic: true)!;
        var snapshotEvent = bridgeType.GetEvent("SnapshotReceived")!;
        var snapshotType = snapshotEvent.EventHandlerType!.GenericTypeArguments[0];
        var parameter = Expression.Parameter(snapshotType, "snapshot");
        var handler = Expression.Lambda(snapshotEvent.EventHandlerType,
            Expression.Call(typeof(Program).GetMethod(nameof(Snapshot), BindingFlags.NonPublic | BindingFlags.Static)!, Expression.Convert(parameter, typeof(object))), parameter).Compile();
        snapshotEvent.AddEventHandler(bridge, handler);
        bridgeType.GetEvent("ConnectionChanged")!.AddEventHandler(bridge, new Action<bool>(connected => Output(new { type = "connection", connected })));
        bridgeType.GetEvent("ProfilesChanged")!.AddEventHandler(bridge, new Action(() => Output(new { type = "profiles", choices = bridgeType.GetMethod("GetConnections")!.Invoke(bridge, null) })));
        bridgeType.GetMethod("Start")!.Invoke(bridge, null);
        Output(new { type = "ready" });
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var input = JsonDocument.Parse(line); var root = input.RootElement;
            string op = root.GetProperty("op").GetString()!;
            if (op == "exit") return 0;
            string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            try
            {
                long generation = root.TryGetProperty("generation", out var requested) && requested.TryGetInt64(out var supplied) ? supplied : Interlocked.Read(ref latestGeneration);
                if (op == "select")
                {
                    bool persisted = (bool)bridgeType.GetMethod("SelectConnection")!.Invoke(bridge, [generation])!;
                    Output(new { type = "ack", id = Read("id"), ok = true, persisted });
                    continue;
                }
                var task = (Task)bridgeType.GetMethod("ExecuteAsync")!.Invoke(bridge, [generation, op, Read("parentId"), Read("bookmarkId"), Read("title"), Read("url")])!;
                await task; Output(new { type = "ack", id = Read("id"), ok = true });
            }
            catch (Exception ex) { Output(new { type = "ack", id = Read("id"), ok = false, error = (ex is TargetInvocationException ? ex.InnerException : ex)?.Message }); }
        }
        return 0;
    }
}
