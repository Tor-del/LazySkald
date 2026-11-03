using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenReader.App;

public readonly record struct HotkeyResult(bool Trigger,bool Suppress);
public sealed class HotkeyChordRecognizer(Hotkey hotkey,IEnumerable<uint>? initiallyPressed=null)
{
    private readonly HashSet<uint> pressed=new(initiallyPressed ?? []);
    private bool consumed;
    public HotkeyResult Process(uint key,bool down)
    {
        bool wasDown=pressed.Contains(key);
        if (down) pressed.Add(key); else pressed.Remove(key);
        if (key!=hotkey.Key) return default;
        if (!down) { bool suppress=consumed; consumed=false; return new(false,suppress); }
        if (consumed) return new(false,true);
        if (wasDown || Modifiers()!=hotkey.Modifiers) return default;
        consumed=true; return new(true,true);
    }
    private uint Modifiers()=>
        (Any(0x12,0xA4,0xA5) ? 1u:0)|(Any(0x11,0xA2,0xA3) ? 2u:0)|
        (Any(0x10,0xA0,0xA1) ? 4u:0)|(Any(0x5B,0x5C) ? 8u:0);
    private bool Any(params uint[] keys)=>keys.Any(pressed.Contains);
}

public enum HotkeyAction { Toggle=1, Capture, CancelCurrent, CancelAll, RepeatCapture }

public sealed class HotkeyService : IDisposable
{
    private readonly Dispatcher ui=Dispatcher.CurrentDispatcher;
    private readonly Thread thread;
    private readonly TaskCompletionSource<Dispatcher> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly KeyboardProc callback;
    private Dispatcher worker=null!;
    private HwndSource? source;
    private IntPtr keyboardHook;
    private AppSettings? current;
    private readonly Dictionary<HotkeyAction,HotkeyChordRecognizer> recognizers=new();
    private bool suspended;
    private volatile bool disposed;
    private int version;
    public event Action<HotkeyAction>? ActionRequested;
    public HotkeyService()
    {
        callback=OnKeyboard;
        thread=new Thread(() =>
        {
            try
            {
                worker=Dispatcher.CurrentDispatcher;
                source=new HwndSource(new HwndSourceParameters("ScreenReader.Hotkeys") { ParentWindow=new IntPtr(-3) });
                source.AddHook(OnMessage); ready.TrySetResult(worker);
                Dispatcher.Run();
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally { Unregister(); source?.Dispose(); }
        }) { IsBackground=true,Name="ScreenReader hotkeys" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        worker=ready.Task.GetAwaiter().GetResult();
    }
    public void Configure(AppSettings settings)
    {
        Validate(settings);
        if (!Enum.IsDefined(settings.HotkeyMode)) throw new ArgumentException(UiText.T("error.hotkeyMode"));
        worker.Invoke(() =>
        {
            var previous=current; Unregister(); Interlocked.Increment(ref version);
            try { if (!suspended) Register(settings); current=settings; }
            catch { Unregister(); if (previous is not null && !suspended) Register(previous); throw; }
        });
    }
    public void SetSuspended(bool value)=>worker.Invoke(() =>
    {
        if (value==suspended) return;
        Interlocked.Increment(ref version);
        if (value) { Unregister(); suspended=true; }
        else { try { if (current is not null) Register(current); suspended=false; } catch { Unregister(); throw; } }
    });
    public static void Validate(Hotkey key)
    {
        if (key.Modifiers is 0 or >15 || key.Key is 0 or >254 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x7B)
            throw new ArgumentException(UiText.T("error.hotkey"));
    }
    public static IEnumerable<(HotkeyAction Action,Hotkey Key)> Bindings(AppSettings settings)
    {
        if (settings.ToggleHotkey is { } toggle) yield return (HotkeyAction.Toggle,toggle);
        if (settings.CaptureHotkey is { } capture) yield return (HotkeyAction.Capture,capture);
        if (settings.RepeatCaptureHotkey is { } repeat) yield return (HotkeyAction.RepeatCapture,repeat);
        if (settings.CancelCurrentHotkey is { } current) yield return (HotkeyAction.CancelCurrent,current);
        if (settings.CancelAllHotkey is { } all) yield return (HotkeyAction.CancelAll,all);
    }
    public static void Validate(AppSettings settings)
    {
        var keys=new HashSet<Hotkey>();
        foreach (var (_,key) in Bindings(settings))
        {
            Validate(key);
            if (!keys.Add(key)) throw new ArgumentException(UiText.T("error.hotkeyDuplicate",key));
        }
    }
    private void Register(AppSettings settings)
    {
        var bindings=Bindings(settings).ToArray();
        if (settings.HotkeyMode==HotkeyMode.Registered)
        {
            foreach (var (action,key) in bindings)
                if (!RegisterHotKey(source!.Handle,(int)action,key.Modifiers|0x4000,key.Key))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.hotkeyBusy",key));
            return;
        }
        if (bindings.Length==0) return;
        var pressed=Enumerable.Range(1,254).Where(k=>k is not (0x10 or 0x11 or 0x12) && (GetAsyncKeyState(k)&0x8000)!=0).Select(k=>(uint)k).ToArray();
        foreach (var (action,key) in bindings) recognizers[action]=new(key,pressed);
        keyboardHook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        if (keyboardHook==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.hook"));
    }
    private IntPtr OnKeyboard(int code,IntPtr wParam,IntPtr lParam)
    {
        if (code>=0 && recognizers.Count>0 && !suspended && !disposed)
        {
            int message=wParam.ToInt32();
            if (message is 0x100 or 0x104 or 0x101 or 0x105)
            {
                uint key=(uint)Marshal.ReadInt32(lParam);
                uint flags=(uint)Marshal.ReadInt32(lParam,8), scan=(uint)Marshal.ReadInt32(lParam,4);
                key=key switch { 0x11=>(flags&1)!=0 ? 0xA3u:0xA2u,0x12=>(flags&1)!=0 ? 0xA5u:0xA4u,0x10=>scan==0x36 ? 0xA1u:0xA0u,_=>key };
                bool suppress=false;
                foreach (var (action,recognizer) in recognizers)
                {
                    var result=recognizer.Process(key,message is 0x100 or 0x104);
                    if (result.Trigger) Dispatch(action);
                    suppress|=result.Suppress;
                }
                if (suppress) return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook,code,wParam,lParam);
    }
    private IntPtr OnMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if (message==0x312 && !suspended && !disposed && current is not null && Bindings(current).Any(b=>(int)b.Action==wParam.ToInt32()))
        { handled=true; Dispatch((HotkeyAction)wParam.ToInt32()); }
        return IntPtr.Zero;
    }
    private void Dispatch(HotkeyAction action)
    {
        int requestedVersion=Volatile.Read(ref version);
        ui.BeginInvoke(() => { if (!disposed && requestedVersion==Volatile.Read(ref version)) ActionRequested?.Invoke(action); });
    }
    private void Unregister()
    {
        if (source is not null) foreach (var action in Enum.GetValues<HotkeyAction>()) UnregisterHotKey(source.Handle,(int)action);
        if (keyboardHook!=IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook=IntPtr.Zero; }
        recognizers.Clear();
    }
    public void Dispose()
    {
        if (disposed) return; disposed=true;
        worker.Invoke(() => { Unregister(); worker.BeginInvokeShutdown(DispatcherPriority.Send); });
        thread.Join(1000);
    }
    private delegate IntPtr KeyboardProc(int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int hook,KeyboardProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window,int id);
}
