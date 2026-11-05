namespace ScreenReader.App;

public sealed record PlaybackProgress(bool Started,bool Paused,TimeSpan Position,TimeSpan? Duration);

// One session per track. Detach serializes UI commands with native output disposal.
public sealed class PlaybackSession
{
    private readonly object gate=new();
    private Action<bool>? changePause;
    private Func<TimeSpan>? position;
    private bool started,paused;
    private TimeSpan? duration;
    public PlaybackProgress Progress
    {
        get
        {
            lock (gate)
            {
                var elapsed=position?.Invoke() ?? TimeSpan.Zero;
                if (duration is { } total && elapsed>total) elapsed=total;
                return new(started,paused,elapsed,duration);
            }
        }
    }
    public void TogglePause()
    {
        lock (gate)
        {
            if (!started || changePause is null) return;
            paused=!paused; changePause(paused);
        }
    }
    internal void Attach(Action<bool> pause,Func<TimeSpan> getPosition)
    { lock (gate) { changePause=pause; position=getPosition; } }
    internal void Start()
    { lock (gate) { started=true; changePause?.Invoke(paused); } }
    internal void SetDuration(TimeSpan? total)
    { lock (gate) duration=total; }
    internal void Detach()
    { lock (gate) { changePause=null; position=null; started=false; paused=false; } }
}
