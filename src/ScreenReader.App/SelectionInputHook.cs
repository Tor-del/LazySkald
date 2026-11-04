using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ScreenReader.App;

// Close on Escape/right-button release so neither edge reaches the game.
internal sealed class SelectionInputHook : IDisposable
{
    private readonly HookProc keyboardCallback,mouseCallback;
    private IntPtr keyboard,mouse;
    private bool selecting;
    private bool cancelling;
    public SelectionInputHook(Action cancel,Action<bool,int,int> click,Action<int,int> move)
    {
        keyboardCallback=(code,message,data)=>
        {
            if (code>=0 && Marshal.ReadInt32(data)==0x1B)
            {
                if (message.ToInt32() is 0x101 or 0x105) cancel();
                return new IntPtr(1);
            }
            return CallNextHookEx(keyboard,code,message,data);
        };
        mouseCallback=(code,message,data)=>
        {
            if (code>=0 && message.ToInt32() is 0x204 or 0x205)
            {
                cancelling=true;
                if (message.ToInt32()==0x205) cancel();
                return new IntPtr(1);
            }
            // Games can retain mouse capture after losing focus. WPF then receives no
            // MouseMove messages, even though this hook receives both button edges.
            if (code>=0 && message.ToInt32()==0x200 && selecting)
                move(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
            if (code>=0 && message.ToInt32() is 0x201 or 0x202)
            {
                bool down=message.ToInt32()==0x201;
                if (!down && !selecting) return CallNextHookEx(mouse,code,message,data);
                selecting=down;
                if (!cancelling) click(down,Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
                return new IntPtr(1);
            }
            return CallNextHookEx(mouse,code,message,data);
        };
        try
        {
            keyboard=SetWindowsHookEx(13,keyboardCallback,GetModuleHandle(null),0);
            if (keyboard==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            mouse=SetWindowsHookEx(14,mouseCallback,GetModuleHandle(null),0);
            if (mouse==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (keyboard!=IntPtr.Zero) { UnhookWindowsHookEx(keyboard); keyboard=IntPtr.Zero; }
        if (mouse!=IntPtr.Zero) { UnhookWindowsHookEx(mouse); mouse=IntPtr.Zero; }
    }
    private delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int hook,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
