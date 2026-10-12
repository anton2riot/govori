using Avalonia.Controls;
using System.Runtime.InteropServices;
using System.Text;

namespace Govori;

internal sealed class LinuxPlatform : IDesktopPlatform
{
    internal readonly object Gate = new();
    internal nint Display { get; }
    internal nuint Root { get; }
    private readonly bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    public string? Limitation => wayland ? Localization.T("WaylandLimitation") : Display == 0 ? Localization.T("X11Unavailable") : null;
    public LinuxPlatform()
    {
        if (wayland) return;
        X11.XInitThreads(); Display = X11.XOpenDisplay(0);
        if (Display != 0) Root = X11.XDefaultRootWindow(Display);
    }
    public nint ForegroundWindow()
    {
        if (Display == 0) return 0;
        lock (Gate)
        {
            var property = ReadProperty(Root, "_NET_ACTIVE_WINDOW");
            if (property.Data == 0) return 0;
            try { return property.Count > 0 ? Marshal.ReadIntPtr(property.Data) : 0; }
            finally { X11.XFree(property.Data); }
        }
    }
    private (nint Data, nuint Count) ReadProperty(nuint window, string name)
    {
        var atom = X11.XInternAtom(Display, name, false);
        X11.XGetWindowProperty(Display, window, atom, 0, 1024, false, 0, out _, out _, out var count, out _, out var data);
        return (data, count);
    }
    public string WindowTitle(nint window)
    {
        if (Display == 0 || window == 0) return "";
        lock (Gate)
        {
            var property = ReadProperty((nuint)window, "_NET_WM_NAME");
            if (property.Data == 0) return "";
            try { return Marshal.PtrToStringUTF8(property.Data, (int)property.Count) ?? ""; }
            finally { X11.XFree(property.Data); }
        }
    }
    public bool IsOwnWindow(nint window)
    {
        if (Display == 0 || window == 0) return false;
        lock (Gate)
        {
            var property = ReadProperty((nuint)window, "_NET_WM_PID");
            if (property.Data == 0) return false;
            try { return property.Count > 0 && Marshal.ReadInt32(property.Data) == Environment.ProcessId; }
            finally { X11.XFree(property.Data); }
        }
    }
    internal int Keycode(int key) => X11.XKeysymToKeycode(Display, Keysym(key));
    internal static nuint Keysym(int key) => key switch
    {
        0x11 or 0xA2 => 0xFFE3, 0xA3 => 0xFFE4, 0x12 or 0xA4 => 0xFFE9, 0xA5 => 0xFFEA,
        0x10 or 0xA0 => 0xFFE1, 0xA1 => 0xFFE2, 0x5B => 0xFFEB, 0x5C => 0xFFEC,
        0x14 => 0xFFE5, 0x1B => 0xFF1B, >= 0x70 and <= 0x87 => (nuint)(0xFFBE + key - 0x70),
        >= 0x41 and <= 0x5A => (nuint)(key + 32), _ => (nuint)key
    };
    public bool IsDown(int key)
    {
        if (Display == 0) return false;
        lock (Gate)
        {
            var keys = new byte[32]; X11.XQueryKeymap(Display, keys);
            bool Has(int vk) { int code = Keycode(vk); return code != 0 && (keys[code / 8] & (1 << (code % 8))) != 0; }
            return key switch { 0x11 => Has(0xA2) || Has(0xA3), 0x12 => Has(0xA4) || Has(0xA5), 0x10 => Has(0xA0) || Has(0xA1), _ => Has(key) };
        }
    }
    public bool ModifiersDown => IsDown(0x11) || IsDown(0x12) || IsDown(0x10) || IsDown(0x5B) || IsDown(0x5C);
    public void Paste()
    {
        if (Display == 0) throw new InvalidOperationException(Localization.T("PasteRequiresX11"));
        lock (Gate)
        {
            foreach (var (vk, down) in new[] { (0x11, true), (0x56, true), (0x56, false), (0x11, false) })
                if (X11.XTestFakeKeyEvent(Display, (uint)Keycode(vk), down, 0) == 0) throw new InvalidOperationException(Localization.T("X11PasteFailed"));
            X11.XFlush(Display);
        }
    }
    public IGlobalHotkeys CreateHotkeys(Hotkey dictation, Hotkey history) => Display == 0 ? new LocalHotkeys(this, dictation, history) : new X11Hotkeys(this, dictation, history);
    public void ConfigureStartup(bool enabled)
    {
        string root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        string path = Path.Combine(root, "autostart", "govori-cross-platform.desktop");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string executable = Environment.ProcessPath!.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        File.WriteAllText(path, $"[Desktop Entry]\nType=Application\nName=Govori CrossPlatform\nExec=\"{executable}\" --tray\nTerminal=false\n");
    }
    public void ConfigureOverlay(Window window) { }
    public void Dispose() { lock (Gate) { if (Display != 0) X11.XCloseDisplay(Display); } }
}

internal sealed class LocalHotkeys(IDesktopPlatform platform, Hotkey dictation, Hotkey history) : HotkeyState(platform, dictation, history)
{
    public override void Dispose() { }
}

internal sealed class X11Hotkeys : HotkeyState
{
    private readonly LinuxPlatform linux;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task loop;
    private static readonly uint[] Locks = [0, 2, 16, 18];
    private readonly Dictionary<int, int> codes = new();
    public X11Hotkeys(LinuxPlatform platform, Hotkey dictation, Hotkey history) : base(platform, dictation, history)
    {
        linux = platform; Grab(); loop = Task.Run(Pump);
    }
    public override Hotkey Dictation { get => base.Dictation; set { base.Dictation = value; if (linux != null) Grab(); } }
    public override Hotkey History { get => base.History; set { base.History = value; if (linux != null) Grab(); } }
    private void Grab()
    {
        lock (linux.Gate)
        {
            X11.XUngrabKey(linux.Display, 0, 1u << 15, linux.Root); codes.Clear();
            foreach (var hotkey in new[] { Dictation, History })
            {
                int code = linux.Keycode(hotkey.Key); if (code == 0) throw new FormatException(Localization.T("HotkeyExamples"));
                codes[code] = hotkey.Key;
                uint modifiers = (hotkey.Shift ? 1u : 0) | (hotkey.Ctrl ? 4u : 0) | (hotkey.Alt ? 8u : 0) | (hotkey.Win ? 64u : 0);
                foreach (var extra in Locks) X11.XGrabKey(linux.Display, code, modifiers | extra, linux.Root, false, 1, 1);
            }
            X11.XFlush(linux.Display);
        }
    }
    private async Task Pump()
    {
        var memory = Marshal.AllocHGlobal(192);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                lock (linux.Gate)
                {
                    while (X11.XPending(linux.Display) > 0)
                    {
                        X11.XNextEvent(linux.Display, memory); var e = Marshal.PtrToStructure<X11.KeyEvent>(memory);
                        if (e.Type is not (2 or 3)) continue;
                        int vk = codes.GetValueOrDefault((int)e.Keycode);
                        if (vk == 0) foreach (var candidate in new[] { 0x11, 0xA3, 0x12, 0xA5, 0x10, 0xA1, 0x5B, 0x5C, 0x1B }) if (linux.Keycode(candidate) == e.Keycode) { vk = candidate; break; }
                        var hotkey = vk == History.Key ? History : Dictation;
                        uint wanted = (hotkey.Shift ? 1u : 0) | (hotkey.Ctrl ? 4u : 0) | (hotkey.Alt ? 8u : 0) | (hotkey.Win ? 64u : 0);
                        HandleKey(vk, e.Type == 2, e.Type == 3, false, (e.State & 77) == wanted);
                    }
                }
                await Task.Delay(10, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { Marshal.FreeHGlobal(memory); }
    }
    public override void Dispose()
    {
        cancellation.Cancel(); loop.GetAwaiter().GetResult();
        lock (linux.Gate) { X11.XUngrabKey(linux.Display, 0, 1u << 15, linux.Root); X11.XFlush(linux.Display); }
        cancellation.Dispose();
    }
}

internal static class X11
{
    private const string Lib = "libX11.so.6";
    [StructLayout(LayoutKind.Sequential)] internal struct KeyEvent { public int Type; public nuint Serial; public int SendEvent; public nint Display; public nuint Window, Root, Subwindow, Time; public int X, Y, RootX, RootY; public uint State, Keycode; public int SameScreen; }
    [DllImport(Lib)] internal static extern int XInitThreads();
    [DllImport(Lib)] internal static extern nint XOpenDisplay(nint name);
    [DllImport(Lib)] internal static extern int XCloseDisplay(nint display);
    [DllImport(Lib)] internal static extern nuint XDefaultRootWindow(nint display);
    [DllImport(Lib)] internal static extern nuint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport(Lib)] internal static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length, bool delete, nuint reqType, out nuint actualType, out int format, out nuint count, out nuint remaining, out nint data);
    [DllImport(Lib)] internal static extern int XFree(nint data);
    [DllImport(Lib)] internal static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport(Lib)] internal static extern int XQueryKeymap(nint display, [Out] byte[] keys);
    [DllImport(Lib)] internal static extern int XGrabKey(nint display, int keycode, uint modifiers, nuint window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] internal static extern int XUngrabKey(nint display, int keycode, uint modifiers, nuint window);
    [DllImport(Lib)] internal static extern int XPending(nint display);
    [DllImport(Lib)] internal static extern int XNextEvent(nint display, nint e);
    [DllImport(Lib)] internal static extern int XFlush(nint display);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeKeyEvent(nint display, uint keycode, bool down, nuint delay);
}
