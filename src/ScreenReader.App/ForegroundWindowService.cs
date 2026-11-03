using System.Runtime.InteropServices;

namespace ScreenReader.App;

public interface IForegroundWindowService
{
    IWindowFocusTarget? Capture();
}
public interface IWindowFocusTarget
{
    Task RestoreAsync();
}

public sealed class ForegroundWindowService : IForegroundWindowService
{
    public IWindowFocusTarget? Capture()
    {
        return CaptureWindow(GetForegroundWindow());
    }
    public IWindowFocusTarget? CaptureWindow(IntPtr window)
    {
        if (window==IntPtr.Zero || !IsWindow(window)) return null;
        GetWindowThreadProcessId(window,out uint processId);
        return processId==0 ? null : new Target(window,processId);
    }

    private sealed class Target(IntPtr window,uint processId) : IWindowFocusTarget
    {
        public async Task RestoreAsync()
        {
            if (!IsOriginalWindow()) return;
            // Exclusive fullscreen games often minimize when the overlay takes focus.
            // Post the restore to their UI thread without blocking on a busy game.
            if (IsIconic(window)) ShowWindowAsync(window,9); // SW_RESTORE
            for (int attempt=0; attempt<7; attempt++)
            {
                if (!IsOriginalWindow()) return;
                if (!IsIconic(window) && (GetForegroundWindow()==window || SetForegroundWindow(window))) return;
                if (attempt<6) await Task.Delay(50);
            }
            AppLog.Write("Windows не подтвердил возврат фокуса исходному окну.");
        }
        private bool IsOriginalWindow()
        {
            if (!IsWindow(window)) return false;
            GetWindowThreadProcessId(window,out uint currentProcessId);
            return currentProcessId==processId;
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
