using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Govori;

internal static class WindowsNative
{
    public const int VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10;
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardEvent { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    public static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public static bool ModifiersDown => IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_SHIFT) || IsDown(0x5B) || IsDown(0x5C);
    public static string WindowTitle(nint hwnd) { var buffer = new StringBuilder(256); GetWindowText(hwnd, buffer, buffer.Capacity); return buffer.ToString(); }
    public static bool IsOwnWindow(nint hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid == Environment.ProcessId; }
    public static void Paste()
    {
        static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        Input[] events = [Key(VK_CONTROL), Key(0x56), Key(0x56, true), Key(VK_CONTROL, true)];
        if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length) throw new Win32Exception(Localization.T("PasteBlocked"));
    }
}
