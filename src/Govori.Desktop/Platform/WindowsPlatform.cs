using Avalonia.Controls;
using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Govori;

internal sealed class WindowsPlatform : IDesktopPlatform
{
    private readonly string? startupDirectory;
    internal WindowsPlatform(string? startupDirectory = null) => this.startupDirectory = startupDirectory;
    public string? Limitation => null;
    public nint ForegroundWindow() => WindowsNative.GetForegroundWindow();
    public string WindowTitle(nint window) => WindowsNative.WindowTitle(window);
    public bool IsOwnWindow(nint window) => WindowsNative.IsOwnWindow(window);
    public bool IsDown(int key) => WindowsNative.IsDown(key);
    public bool ModifiersDown => WindowsNative.ModifiersDown;
    public void Paste() => WindowsNative.Paste();
    public IGlobalHotkeys CreateHotkeys(Hotkey dictation, Hotkey history) => new WindowsHotkeys(this, dictation, history);
    public void ConfigureStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsStartup.Configure(enabled, Environment.ProcessPath!, startupDirectory);
        if (startupDirectory != null) return;
        // Remove the previous registration after the shortcut was saved.
        using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        startup.DeleteValue("Govori.CrossPlatform", false);
    }
    public void ConfigureOverlay(Window window)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x80 | 0x20);
        SetWindowLong(hwnd, -16, (GetWindowLong(hwnd, -16) & ~0x00CF0000) | unchecked((int)0x80000000));
        // Win32 applies its minimum tracking size even to undecorated Avalonia
        // windows. Resize the actual HWND after Show, as in the WPF indicator.
        SetWindowPos(hwnd, (nint)(-1), window.Position.X, window.Position.Y,
            window is OverlayWindow ? OverlayWindow.PhysicalSize : Math.Max(1, (int)Math.Round(window.Bounds.Width * window.RenderScaling)),
            window is OverlayWindow ? OverlayWindow.PhysicalSize : Math.Max(1, (int)Math.Round(window.Bounds.Height * window.RenderScaling)), 0x10 | 0x40 | 0x20);
    }
    public void Dispose() { }
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
}

internal sealed class WindowsHotkeys : HotkeyState
{
    private readonly WindowsNative.HookProc callback;
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> probes = new();
    private uint threadId;
    private int disposed;
    private nint hook;
    public WindowsHotkeys(IDesktopPlatform platform, Hotkey dictation, Hotkey history) : base(platform, dictation, history)
    {
        callback = OnKey;
        // A low-level hook must keep pumping even while Avalonia opens devices,
        // loads history or handles a slow UI operation. Windows removes timed-out hooks.
        thread = new Thread(Run) { IsBackground = true, Name = "Govori keyboard" };
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }
    private void Run()
    {
        try
        {
            threadId = WindowsNative.GetCurrentThreadId();
            WindowsNative.PeekMessage(out _, 0, 0, 0, 0);
            hook = WindowsNative.SetWindowsHookEx(13, callback, WindowsNative.GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), Localization.T("HotkeyRegistrationFailed"));
            ready.SetResult();
            while (WindowsNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8001) { while (probes.TryDequeue(out var probe)) probe.TrySetResult(); }
                else { WindowsNative.TranslateMessage(message); WindowsNative.DispatchMessage(message); }
            }
        }
        catch (Exception ex) { ready.TrySetException(ex); }
        finally
        {
            if (hook != 0) { WindowsNative.UnhookWindowsHookEx(hook); hook = 0; }
            while (probes.TryDequeue(out var probe)) probe.TrySetCanceled();
        }
    }
    internal Task ProbeAsync()
    {
        var probe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probes.Enqueue(probe);
        if (!WindowsNative.PostThreadMessage(threadId, 0x8001, 0, 0)) probe.TrySetException(new Win32Exception(Marshal.GetLastWin32Error()));
        return probe.Task;
    }
    private nint OnKey(int code, nint message, nint pointer)
    {
        if (code < 0) return WindowsNative.CallNextHookEx(hook, code, message, pointer);
        var e = Marshal.PtrToStructure<WindowsNative.KeyboardEvent>(pointer);
        bool handled = HandleKey((int)e.Key, message is 0x100 or 0x104, message is 0x101 or 0x105, (e.Flags & 0x10) != 0);
        return handled ? 1 : WindowsNative.CallNextHookEx(hook, code, message, pointer);
    }
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        WindowsNative.PostThreadMessage(threadId, 0x0012, 0, 0);
        thread.Join();
    }
}
