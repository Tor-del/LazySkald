namespace ScreenReader.App;

public sealed class AppLifetimeSignal : IDisposable
{
    public const string EventName="Local\\ScreenReader.Desktop.Exit";
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle registration;
    public AppLifetimeSignal(Action close,string eventName=EventName)
    {
        signal=new EventWaitHandle(false,EventResetMode.AutoReset,eventName);
        registration=ThreadPool.RegisterWaitForSingleObject(signal,(_,_)=>close(),null,Timeout.Infinite,false);
    }
    public void Dispose() { registration.Unregister(null); signal.Dispose(); }
}
