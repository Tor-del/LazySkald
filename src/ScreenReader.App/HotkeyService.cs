using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenReader.App;

public readonly record struct HotkeyResult(bool Trigger,bool Suppress);
public sealed class HotkeyChordRecognizer(Hotkey hotkey,IEnumerable<uint>? initiallyPressed=null,bool hold=false)
{
    private readonly HashSet<uint> pressed=new(initiallyPressed ?? []);
    private bool consumed;
    public bool Held { get; private set; }
    public void Synchronize(IEnumerable<uint> down)
    {
        pressed.Clear(); pressed.UnionWith(down);
        // Suppressed presses may not appear in GetAsyncKeyState. Keep their
        // repeat/release ownership until the actual key-up reaches the hook.
        if (consumed) pressed.Add(hotkey.Key);
    }
    public HotkeyResult Process(uint key,bool down)
    {
        bool wasDown=pressed.Contains(key);
        if (down) pressed.Add(key); else pressed.Remove(key);
        if (hold)
        {
            bool wasHeld=Held;
            uint ownModifier=hotkey.Key switch { 0x12 or 0xA4 or 0xA5=>1u,0x11 or 0xA2 or 0xA3=>2u,0x10 or 0xA0 or 0xA1=>4u,0x5B or 0x5C=>8u,_=>0u };
            bool primary=pressed.Any(IsPrimaryKey);
            bool starts=down && !wasDown && IsPrimaryKey(key) && (Modifiers()&~ownModifier)==hotkey.Modifiers;
            Held=primary && (Modifiers()&hotkey.Modifiers)==hotkey.Modifiers && (wasHeld || starts);
            return new(Held && !wasHeld,false);
        }
        if (key!=hotkey.Key) return default;
        if (!down) { bool suppress=consumed; consumed=false; return new(false,suppress); }
        if (consumed) return new(false,true);
        if (wasDown || Modifiers()!=hotkey.Modifiers) return default;
        consumed=true; return new(true,true);
    }
    private bool IsPrimaryKey(uint key)=>hotkey.Key switch
    {
        0x12=>key is 0x12 or 0xA4 or 0xA5,
        0x11=>key is 0x11 or 0xA2 or 0xA3,
        0x10=>key is 0x10 or 0xA0 or 0xA1,
        _=>key==hotkey.Key
    };
    private uint Modifiers()=>
        (Any(0x12,0xA4,0xA5) ? 1u:0)|(Any(0x11,0xA2,0xA3) ? 2u:0)|
        (Any(0x10,0xA0,0xA1) ? 4u:0)|(Any(0x5B,0x5C) ? 8u:0);
    private bool Any(params uint[] keys)=>keys.Any(pressed.Contains);
}

public enum HotkeyAction { Toggle=1, Capture, CancelCurrent, CancelAll, RepeatCapture, OverlayInteraction }

public sealed class HotkeyService : IDisposable
{
    private readonly Dispatcher ui=Dispatcher.CurrentDispatcher;
    private readonly Thread thread;
    private readonly TaskCompletionSource<Dispatcher> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly KeyboardProc callback;
    private readonly KeyboardProc mouseCallback;
    private readonly WinEventProc foregroundCallback;
    private Dispatcher worker=null!;
    private HwndSource? source;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;
    private IntPtr foregroundHook;
    private DispatcherTimer? foregroundRefresh;
    private IntPtr pendingForeground;
    private AppSettings? current;
    private readonly Dictionary<HotkeyAction,HotkeyChordRecognizer> recognizers=new();
    private bool suspended;
    private volatile bool disposed;
    private int version;
    public event Action<HotkeyAction>? ActionRequested;
    public event Action<bool>? OverlayInteractionChanged;
    public HotkeyService()
    {
        callback=OnKeyboard;
        mouseCallback=OnMouse;
        foregroundCallback=OnForegroundChanged;
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
        OverlayInteractionChanged?.Invoke(false);
        if (!Enum.IsDefined(settings.HotkeyMode)) throw new ArgumentException(UiText.T("error.hotkeyMode"));
        worker.Invoke(() =>
        {
            var previous=current; Unregister(); Interlocked.Increment(ref version);
            try { if (!suspended) Register(settings); current=settings; }
            catch { Unregister(); if (previous is not null && !suspended) Register(previous); throw; }
        });
    }
    public void SetSuspended(bool value)
    {
        if (value) OverlayInteractionChanged?.Invoke(false);
        worker.Invoke(() =>
        {
            if (value==suspended) return;
            Interlocked.Increment(ref version);
            if (value) { Unregister(); suspended=true; }
            else { try { if (current is not null) Register(current); suspended=false; } catch { Unregister(); throw; } }
        });
    }
    public static void Validate(Hotkey key,bool allowModifier=false)
    {
        if (key.IsReserved) throw new ArgumentException(UiText.T("error.hotkeyReserved"));
        if (allowModifier && key.Modifiers==0 && key.Key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >=0xA0 and <=0xA5) return;
        if (key.Modifiers>15 || key.Key is 0 or >254 or 0x03 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >=0xA0 and <=0xA5)
            throw new ArgumentException(UiText.T("error.hotkey"));
    }
    public static IEnumerable<(HotkeyAction Action,Hotkey Key)> Bindings(AppSettings settings)
    {
        if (settings.ToggleHotkey is { } toggle) yield return (HotkeyAction.Toggle,toggle);
        if (settings.CaptureHotkey is { } capture) yield return (HotkeyAction.Capture,capture);
        if (settings.RepeatCaptureHotkey is { } repeat) yield return (HotkeyAction.RepeatCapture,repeat);
        if (settings.CancelCurrentHotkey is { } current) yield return (HotkeyAction.CancelCurrent,current);
        if (settings.CancelAllHotkey is { } all) yield return (HotkeyAction.CancelAll,all);
        if (settings.OverlayInteractionHotkey is { } overlay) yield return (HotkeyAction.OverlayInteraction,overlay);
    }
    public static void Validate(AppSettings settings)
    {
        var keys=new HashSet<Hotkey>();
        foreach (var (action,key) in Bindings(settings))
        {
            Validate(key,action==HotkeyAction.OverlayInteraction);
            if (!keys.Add(key)) throw new ArgumentException(UiText.T("error.hotkeyDuplicate",key));
        }
    }
    private void Register(AppSettings settings)
    {
        var bindings=Bindings(settings).ToArray();
        var hooked=bindings.Where(b=>b.Action==HotkeyAction.OverlayInteraction || settings.ExclusiveHotkeys || settings.HotkeyMode==HotkeyMode.GameHook || b.Key.IsMouse || b.Key.Key==0x7B).ToArray();
        if (settings.HotkeyMode==HotkeyMode.Registered)
        {
            foreach (var (action,key) in bindings.Except(hooked))
                if (!RegisterHotKey(source!.Handle,(int)action,key.Modifiers|0x4000,key.Key))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.hotkeyBusy",key));
        }
        if (hooked.Length==0) return;
        var pressed=PressedKeys();
        foreach (var (action,key) in hooked) recognizers[action]=new(key,pressed,action==HotkeyAction.OverlayInteraction);
        ReplaceInputHooks(hooked.Any(b=>b.Key.IsMouse));
        foregroundHook=SetWinEventHook(3,3,IntPtr.Zero,foregroundCallback,0,0,0); // EVENT_SYSTEM_FOREGROUND, out of context
        if (foregroundHook==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.hook"));
        foregroundRefresh=new DispatcherTimer(DispatcherPriority.Background,worker) { Interval=TimeSpan.FromMilliseconds(300) };
        foregroundRefresh.Tick+=(_,_)=>
        {
            foregroundRefresh?.Stop();
            if (pendingForeground!=GetForegroundWindow()) return;
            try { RefreshInputHooks(); }
            catch (Exception e) { AppLog.Write("Не удалось обновить хуки после смены активного окна",e); }
        };
    }
    private static uint[] PressedKeys()=>Enumerable.Range(1,254)
        .Where(k=>k is not (0x10 or 0x11 or 0x12) && (GetAsyncKeyState(k)&0x8000)!=0).Select(k=>(uint)k).ToArray();
    private void OnForegroundChanged(IntPtr hook,uint eventType,IntPtr window,int objectId,int childId,uint eventThread,uint time)
    {
        if (disposed || suspended || foregroundRefresh is null) return;
        foregroundRefresh.Stop(); pendingForeground=IntPtr.Zero;
        if (window==IntPtr.Zero || window!=GetForegroundWindow()) return;
        GetWindowThreadProcessId(window,out uint processId);
        // Do not reorder hooks while our own selection overlay is handling input.
        if (processId==(uint)Environment.ProcessId) return;
        pendingForeground=window;
        // DirectInput games can acquire their keyboard hook during activation.
        foregroundRefresh.Start();
    }
    internal void RefreshInputHooks()=>worker.Invoke(() =>
    {
        if (disposed || suspended || recognizers.Count==0) return;
        ReplaceInputHooks(mouseHook!=IntPtr.Zero);
        var pressed=PressedKeys();
        foreach (var recognizer in recognizers.Values) recognizer.Synchronize(pressed);
    });
    private void ReplaceInputHooks(bool includeMouse)
    {
        // Install first: failure must leave the existing hooks working.
        IntPtr keyboard=SetWindowsHookEx(13,callback,GetModuleHandle(null),0), mouse=IntPtr.Zero;
        if (keyboard==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),UiText.T("error.hook"));
        if (includeMouse)
        {
            mouse=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
            if (mouse==IntPtr.Zero)
            {
                int error=Marshal.GetLastWin32Error(); UnhookWindowsHookEx(keyboard);
                throw new Win32Exception(error,UiText.T("error.hook"));
            }
        }
        if (keyboardHook!=IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
        if (mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        keyboardHook=keyboard; mouseHook=mouse;
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
                if (ProcessInput(key,message is 0x100 or 0x104)) return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook,code,wParam,lParam);
    }
    public static (uint Key,bool Down)? MouseInput(int message,uint data)=>message switch
    {
        0x201 or 0x202 => (0x01,message==0x201),
        0x204 or 0x205 => (0x02,message==0x204),
        0x207 or 0x208 => (0x04,message==0x207),
        0x20B or 0x20C when (data>>16) is 1 or 2 => ((data>>16)==1 ? 0x05u:0x06u,message==0x20B),
        _=>null
    };
    private IntPtr OnMouse(int code,IntPtr wParam,IntPtr lParam)
    {
        if (code>=0 && !suspended && !disposed && MouseInput(wParam.ToInt32(),(uint)Marshal.ReadInt32(lParam,8)) is { } input && ProcessInput(input.Key,input.Down))
            return new IntPtr(1);
        return CallNextHookEx(mouseHook,code,wParam,lParam);
    }
    private bool ProcessInput(uint key,bool down)
    {
        bool suppress=false;
        foreach (var (action,recognizer) in recognizers)
        {
            bool wasHeld=recognizer.Held;
            var result=recognizer.Process(key,down);
            if (action==HotkeyAction.OverlayInteraction)
            {
                if (wasHeld!=recognizer.Held) DispatchInteraction(recognizer.Held);
                continue;
            }
            if (result.Trigger) Dispatch(action);
            suppress|=result.Suppress;
        }
        return suppress && current?.ExclusiveHotkeys==true;
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
    private void DispatchInteraction(bool held)
    {
        int requestedVersion=Volatile.Read(ref version);
        ui.BeginInvoke(() => { if (!disposed && requestedVersion==Volatile.Read(ref version)) OverlayInteractionChanged?.Invoke(held); });
    }
    private void Unregister()
    {
        foregroundRefresh?.Stop(); foregroundRefresh=null; pendingForeground=IntPtr.Zero;
        if (foregroundHook!=IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook=IntPtr.Zero; }
        if (source is not null) foreach (var action in Enum.GetValues<HotkeyAction>()) UnregisterHotKey(source.Handle,(int)action);
        if (keyboardHook!=IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook=IntPtr.Zero; }
        if (mouseHook!=IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook=IntPtr.Zero; }
        recognizers.Clear();
    }
    public void Dispose()
    {
        if (disposed) return; disposed=true;
        worker.Invoke(() => { Unregister(); worker.BeginInvokeShutdown(DispatcherPriority.Send); });
        thread.Join(1000);
    }
    private delegate IntPtr KeyboardProc(int code,IntPtr wParam,IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook,uint eventType,IntPtr window,int objectId,int childId,uint eventThread,uint time);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWinEventHook(uint first,uint last,IntPtr module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int hook,KeyboardProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window,int id);
}
