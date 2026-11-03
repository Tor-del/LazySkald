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
    [StructLayout(LayoutKind.Explicit,Size=32)] private struct InputData { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] input,int size);
    private static void SendTestChord(ushort mainKey=0x86)
    {
        Input Key(ushort code,bool up=false)=>new() { Type=1,Data=new InputData { Keyboard=new KeyboardInput { Key=code,Flags=up ? 2u:0 } } };
        var input=new[] { Key(0xA2),Key(0xA4),Key(mainKey),Key(mainKey),Key(mainKey,true),Key(0xA4,true),Key(0xA2,true) };
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
                        if (GetForegroundWindow()!=handle) throw new Exception("Test window lost foreground; no further keys sent.");
                        SendTestChord(key); await Task.Delay(100);
                    }
                    Assert(actions.SequenceEqual(new[] { HotkeyAction.Toggle,HotkeyAction.Capture,HotkeyAction.RepeatCapture,HotkeyAction.CancelCurrent,HotkeyAction.CancelAll }),"Native input routes all five actions independently: "+mode);
                }
                service.Configure(new AppSettings { CaptureHotkey=null });
                actions.Clear(); SendTestChord(); await Task.Delay(100);
                Assert(actions.Count==0,"Clearing every binding disables shortcut delivery");
            }
            catch (Exception e) { error=e; }
            finally { window.Close(); if (original is not null) await original.RestoreAsync(); loop.Continue=false; }
        });
        Dispatcher.PushFrame(loop);
        if (error is not null) throw error;
    }
}
