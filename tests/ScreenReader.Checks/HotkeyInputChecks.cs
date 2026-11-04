using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ScreenReader.App;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key,Scan; public uint Flags,Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInputData { public int X,Y; public uint Data,Flags,Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit,Size=32)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInputData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] input,int size);
    private static void SendTestChord(ushort mainKey=0x86,bool modified=true)
    {
        Input Key(ushort code,bool up=false)=>new() { Type=1,Data=new InputData { Keyboard=new KeyboardInput { Key=code,Flags=up ? 2u:0 } } };
        var input=modified ? new[] { Key(0xA2),Key(0xA4),Key(mainKey),Key(mainKey),Key(mainKey,true),Key(0xA4,true),Key(0xA2,true) } : new[] { Key(mainKey),Key(mainKey),Key(mainKey,true) };
        if (SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=input.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private static void RunHotkeyInput()
    {
        var loop=new DispatcherFrame(); Exception? error=null;
        var original=new ForegroundWindowService().Capture();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            var window=new Window { Title="ScreenReader — проверка горячей клавиши",Width=460,Height=160,Content=new TextBlock { Text="Проверка Ctrl + Alt + F23 на собственном окне.",Margin=new Thickness(20) } };
            try
            {
                window.Show(); window.Activate(); await Task.Delay(150);
                IntPtr handle=new WindowInteropHelper(window).Handle;
                async Task FocusTestWindow()
                {
                    window.Activate();
                    await new ForegroundWindowService().CaptureWindow(handle)!.RestoreAsync();
                    await Task.Delay(50);
                }
                await FocusTestWindow();
                if (GetForegroundWindow()!=handle)
                { Console.WriteLine("SKIP: Foreground lock prevents native input test; no keys sent to another application."); return; }
                using var service=new HotkeyService(); int commands=0;
                var settings=new AppSettings { CaptureHotkey=new(3,0x86),HotkeyMode=HotkeyMode.GameHook };
                service.ActionRequested+=action=> { if (action==HotkeyAction.Capture) commands++; };
                service.Configure(settings);
                SendTestChord(); await Task.Delay(150);
                Assert(commands==1,"Native low-level hook delivers one command despite key auto-repeat");
                SendTestChord(); await Task.Delay(150);
                Assert(commands==2,"A second native chord delivers exactly one queue command");
                service.SetSuspended(true); SendTestChord(); await Task.Delay(100);
                Assert(commands==2,"Editing mode suspends native shortcut processing");
                service.SetSuspended(false);
                service.Configure(settings with { HotkeyMode=HotkeyMode.Registered });
                SendTestChord(); await Task.Delay(150);
                Assert(commands==3,"RegisterHotKey alternative also delivers one command per chord");
                var actions=new System.Collections.Generic.List<HotkeyAction>();
                service.ActionRequested+=actions.Add;
                var all=settings with { RepeatCaptureHotkey=new(3,0x82), ToggleHotkey=new(3,0x83),CancelCurrentHotkey=new(3,0x84),CancelAllHotkey=new(3,0x85) };
                foreach (var mode in new[] { HotkeyMode.GameHook,HotkeyMode.Registered })
                {
                    actions.Clear(); service.Configure(all with { HotkeyMode=mode });
                    foreach (ushort key in new ushort[] { 0x83,0x86,0x82,0x84,0x85 })
                    {
                        await FocusTestWindow();
                        if (GetForegroundWindow()!=handle) throw new Exception("Test window lost foreground; no further keys sent.");
                        SendTestChord(key); await Task.Delay(100);
                    }
                    Assert(actions.SequenceEqual(new[] { HotkeyAction.Toggle,HotkeyAction.Capture,HotkeyAction.RepeatCapture,HotkeyAction.CancelCurrent,HotkeyAction.CancelAll }),"Native input routes all five actions independently: "+mode);
                }
                foreach (var mode in new[] { HotkeyMode.GameHook,HotkeyMode.Registered })
                {
                    foreach (ushort key in new ushort[] { 0x41,0x14,0x70,0x7B })
                    {
                        if (GetForegroundWindow()!=handle) throw new Exception("Test window lost foreground; no further keys sent.");
                        service.Configure(new AppSettings { CaptureHotkey=new(0,key),HotkeyMode=mode });
                        actions.Clear(); SendTestChord(key,false); await Task.Delay(100);
                        Assert(actions.SequenceEqual(new[] { HotkeyAction.Capture }),"Native single key fires once: "+mode+" / "+key);
                    }
                    foreach (var (key,down,up,data) in new (uint,uint,uint,uint)[] { (4,32,64,0),(5,128,256,1),(6,128,256,2) })
                    {
                        if (GetForegroundWindow()!=handle) throw new Exception("Test window lost foreground; no mouse input sent.");
                        service.Configure(new AppSettings { CaptureHotkey=new(0,key),HotkeyMode=mode });
                        actions.Clear();
                        var input=new[] { new Input { Data=new InputData { Mouse=new MouseInputData { Flags=down,Data=data } } },new Input { Data=new InputData { Mouse=new MouseInputData { Flags=up,Data=data } } } };
                        if (SendInput((uint)input.Length,input,Marshal.SizeOf<Input>())!=input.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        await Task.Delay(100);
                        Assert(actions.SequenceEqual(new[] { HotkeyAction.Capture }),"Native mouse button fires once: "+mode+" / "+key);
                    }
                }
                int receivedDown=0,receivedUp=0;
                window.PreviewKeyDown+=(_,e)=> { if (e.Key==System.Windows.Input.Key.F23) receivedDown++; };
                window.PreviewKeyUp+=(_,e)=> { if (e.Key==System.Windows.Input.Key.F23) receivedUp++; };
                foreach (var mode in new[] { HotkeyMode.GameHook,HotkeyMode.Registered })
                {
                    if (GetForegroundWindow()!=handle) throw new Exception("Test window lost foreground; no input sent.");
                    service.Configure(settings with { CaptureHotkey=new(0,0x86),HotkeyMode=mode,ExclusiveHotkeys=true });
                    receivedDown=receivedUp=0; SendTestChord(0x86,false); await Task.Delay(100);
                    Assert(receivedDown==0 && receivedUp==0,"Exclusive mode suppresses press, repeats and release: "+mode);
                }
                service.Configure(settings with { CaptureHotkey=new(0,0x86),ExclusiveHotkeys=false });
                await FocusTestWindow(); System.Windows.Input.Keyboard.Focus(window);
                actions.Clear(); receivedDown=receivedUp=0; SendTestChord(0x86,false); await Task.Delay(100);
                Assert(receivedDown>=1 && receivedUp==1 && actions.SequenceEqual(new[] { HotkeyAction.Capture }),$"Disabling exclusivity passes press and release while still triggering once (down={receivedDown}, up={receivedUp}, actions={actions.Count})");
                service.Configure(new AppSettings { CaptureHotkey=null });
                actions.Clear(); SendTestChord(); await Task.Delay(100);
                Assert(actions.Count==0,"Clearing every binding disables shortcut delivery");
                await RunExclusiveSelectionChecksAsync(window,handle);
            }
            catch (Exception e) { error=e; }
            finally { window.Close(); if (original is not null) await original.RestoreAsync(); loop.Continue=false; }
        });
        Dispatcher.PushFrame(loop);
        if (error is not null) throw error;
    }
    private delegate IntPtr ProbeProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int hook,ProbeProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    private static async Task RunExclusiveSelectionChecksAsync(Window host,IntPtr hostHandle)
    {
        int escaped=0,clicked=0,rightClicked=0;
        ProbeProc keyboard=(code,message,data)=>
        {
            if (code>=0 && Marshal.ReadInt32(data)==0x1B) escaped++;
            return CallNextHookEx(IntPtr.Zero,code,message,data);
        };
        ProbeProc mouse=(code,message,data)=>
        {
            if (code>=0 && message.ToInt32() is 0x201 or 0x202) clicked++;
            if (code>=0 && message.ToInt32() is 0x204 or 0x205) rightClicked++;
            return CallNextHookEx(IntPtr.Zero,code,message,data);
        };
        IntPtr keyHook=SetWindowsHookEx(13,keyboard,GetModuleHandle(null),0);
        IntPtr mouseHook=SetWindowsHookEx(14,mouse,GetModuleHandle(null),0);
        if (keyHook==IntPtr.Zero || mouseHook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var cursor=System.Windows.Forms.Cursor.Position;
        SelectionWindow? overlay=null;
        try
        {
            using var image=new System.Drawing.Bitmap(640,360);
            var bounds=System.Windows.Forms.Screen.FromHandle(hostHandle).Bounds;
            using var frame=new CapturedScreen(image,bounds,"Synthetic");
            var target=new ForegroundWindowService().CaptureWindow(hostHandle);
            overlay=new SelectionWindow(frame,target,true); overlay.Show(); await Task.Delay(100);
            if (GetForegroundWindow()!=new WindowInteropHelper(overlay).Handle) throw new Exception("Selection lost foreground; no keys sent.");
            SendTestChord(0x1B,false);
            var result=await overlay.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(result is null && escaped==0,"Exclusive selection consumes Escape press, repeat and release before restoring focus");
            Assert(GetForegroundWindow()==hostHandle,"Escape selection restores the original test window");
            overlay=new SelectionWindow(frame,target,true); overlay.Show(); await Task.Delay(100);
            if (GetForegroundWindow()!=new WindowInteropHelper(overlay).Handle) throw new Exception("Selection lost foreground; no mouse input sent.");
            // Reproduce a game retaining mouse capture: the overlay must draw from
            // hook coordinates without WPF MouseMove or a right-click to release it.
            SetCapture(hostHandle);
            Assert(GetCapture()==hostHandle,"Simulated game retains native mouse capture during selection");
            int overlayMoves=0;
            overlay.MouseMove+=(_,_)=>overlayMoves++;
            System.Windows.Forms.Cursor.Position=new System.Drawing.Point(bounds.Left+100,bounds.Top+100);
            await Task.Delay(50);
            void Button(uint flags)
            {
                var input=new[] { new Input { Data=new InputData { Mouse=new MouseInputData { Flags=flags } } } };
                if (SendInput(1,input,Marshal.SizeOf<Input>())!=1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            Button(2); await Task.Delay(50);
            System.Windows.Forms.Cursor.Position=new System.Drawing.Point(bounds.Left+300,bounds.Top+200); await Task.Delay(50);
            var canvas=(Canvas)((Grid)overlay.Content).Children[1];
            var border=canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Single();
            var from=canvas.PointFromScreen(new Point(bounds.Left+100,bounds.Top+100));
            var to=canvas.PointFromScreen(new Point(bounds.Left+300,bounds.Top+200));
            Assert(overlayMoves==0,"Retained capture prevents WPF mouse movement from reaching the overlay");
            Assert(border.Visibility==Visibility.Visible && Math.Abs(border.Width-(to.X-from.X))<0.01 && Math.Abs(border.Height-(to.Y-from.Y))<0.01,
                "Selection border follows the first drag while the game holds capture, without a right-click");
            var expected=SelectionMath.ToPixels(from.X,from.Y,to.X,to.Y,canvas.ActualWidth,canvas.ActualHeight,image.Width,image.Height);
            Button(4);
            result=await overlay.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert(result==expected && clicked==0,"Exclusive selection consumes both left-button edges and returns the exact displayed region");
            Assert(GetForegroundWindow()==hostHandle,"Mouse selection restores the original test window");
            foreach (bool dragging in new[] { false,true })
            {
                overlay=new SelectionWindow(frame,target,true); overlay.Show(); await Task.Delay(100);
                if (GetForegroundWindow()!=new WindowInteropHelper(overlay).Handle) throw new Exception("Selection lost foreground; no mouse input sent.");
                if (dragging) { Button(2); await Task.Delay(50); }
                Button(8); await Task.Delay(50);
                if (dragging) { Button(4); await Task.Delay(50); }
                Assert(overlay.IsVisible,"Exclusive right-button cancellation waits for release: dragging="+dragging);
                Button(16);
                result=await overlay.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                Assert(result is null && rightClicked==0 && clicked==0,"Exclusive right click cancels without leaking either button edge: dragging="+dragging);
                Assert(GetForegroundWindow()==hostHandle,"Right-click cancellation restores the original test window");
            }
        }
        finally
        {
            if (GetCapture()==hostHandle) ReleaseCapture();
            if (overlay?.IsVisible==true) overlay.Close();
            UnhookWindowsHookEx(keyHook); UnhookWindowsHookEx(mouseHook);
            GC.KeepAlive(keyboard); GC.KeepAlive(mouse);
            System.Windows.Forms.Cursor.Position=cursor;
        }
    }

}
