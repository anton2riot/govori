using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Govori;

internal sealed class HotkeyBox : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    private readonly DictationController controller;
    private string text = "";
    private bool capturing;
    private readonly HashSet<Key> capturedKeys = [];
    internal bool IsCapturing => capturing;
    public string Text { get => text; set { text = value; Refresh(); } }
    public event Action? ShortcutCommitted;
    public HotkeyBox(DictationController controller)
    {
        this.controller = controller; Classes.Add("shortcut");
        Click += (_, _) =>
        {
            if (controller.Recording || capturing) return;
            capturedKeys.Clear(); capturing = true; _ = controller.SetHotkeyCapture(true); Refresh(); Focus();
        };
        // Handle captured keys before Button's keyboard activation. In
        // particular, releasing Space must not click this button again.
        AddHandler(KeyDownEvent, CaptureKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, CaptureKeyUp, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => { EndCapture(); capturedKeys.Clear(); };
        Localization.Changed += Refresh;
    }
    private void Refresh() => Content = capturing ? Localization.T("HotkeyPrompt") : text.Replace("+", "  +  ");
    private void EndCapture() { if (!capturing) return; capturing = false; _ = controller.SetHotkeyCapture(false); Refresh(); }
    private void CaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (!capturing && !capturedKeys.Contains(e.Key)) return;
        e.Handled = true;
        if (!capturing || !capturedKeys.Add(e.Key)) return;
        if (e.Key == Key.Escape) { EndCapture(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(e.Key is >= Key.D0 and <= Key.D9 ? ((int)e.Key - (int)Key.D0).ToString() : e.Key.ToString());
        SetShortcut(string.Join('+', parts));
    }
    private void CaptureKeyUp(object? sender, KeyEventArgs e)
    {
        bool captured = capturedKeys.Remove(e.Key);
        if (!capturing && !captured) return;
        e.Handled = true;
        if (capturing && captured && e.Key is Key.RightAlt or Key.RightCtrl) SetShortcut(e.Key.ToString());
    }
    private void SetShortcut(string value)
    {
        try { Text = Hotkey.Parse(value).Label; ShortcutCommitted?.Invoke(); EndCapture(); }
        catch (FormatException) { Content = Localization.T("HotkeyAddModifier"); }
    }
}
