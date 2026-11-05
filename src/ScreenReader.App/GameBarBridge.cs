using System.Text.Json;

namespace ScreenReader.App;

// Versioned local IPC. Only the current job's presentation is exported; never settings or credentials.
public sealed record GameBarState(int Version,string Session,long UpdatedAt,long? JobId,string Stage,
    string Translation,bool Playing,bool Paused,double Position,double? Duration,string PauseLabel,string CloseLabel);
public sealed record GameBarHeartbeat(int Version,string Session,long UpdatedAt);
public sealed record GameBarCommand(int Version,string Session,long JobId,string Action);

public sealed class GameBarBridge(string directory) : IDisposable
{
    public const int ProtocolVersion=1;
    public const string PackageName="LazySkald.GameBar";
    public string Session { get; }=Guid.NewGuid().ToString("N");
    public bool Connected { get; private set; }
    private static bool Recent(long stamp,long now)=>stamp<=now+1000 && stamp>=now-3000;
    public void Publish(GameBarState state,ReadingQueue queue,long now)
    {
        Directory.CreateDirectory(directory);
        Connected=false;
        try
        {
            var heartbeat=Read<GameBarHeartbeat>("heartbeat.json");
            Connected=heartbeat is { Version:ProtocolVersion } && heartbeat.Session==Session && Recent(heartbeat.UpdatedAt,now);
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        // Commands are one-shot files so repeated clicks and retries cannot toggle pause twice.
        foreach (string path in Directory.EnumerateFiles(directory,"command-*.json").Take(32))
        {
            try
            {
                var command=Read<GameBarCommand>(Path.GetFileName(path));
                File.Delete(path);
                if (command is not { Version:ProtocolVersion } || command.Session!=Session || !Connected) continue;
                var job=queue.Snapshot.FirstOrDefault();
                if (job?.Id!=command.JobId) continue;
                if (command.Action=="close") queue.Close(job.Id);
                else if (command.Action=="pause") job.Playback?.TogglePause();
            }
            catch (Exception e) when (e is IOException or JsonException) { TryDelete(path); }
        }
        string temporary=Path.Combine(directory,"state.tmp");
        File.WriteAllText(temporary,JsonSerializer.Serialize(state with { Version=ProtocolVersion,Session=Session,UpdatedAt=now }));
        File.Move(temporary,Path.Combine(directory,"state.json"),true);
    }
    private T? Read<T>(string name)
    {
        string path=Path.Combine(directory,name);
        if (!File.Exists(path)) return default;
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if (input.Length>16384) return default;
        return JsonSerializer.Deserialize<T>(input);
    }
    private static void TryDelete(string path)
    { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Dispose()
    {
        Connected=false;
        TryDelete(Path.Combine(directory,"state.json"));
        TryDelete(Path.Combine(directory,"state.tmp"));
    }
}
