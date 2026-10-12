using Avalonia.Input;
using Avalonia.Input.Platform;
using System.Runtime.InteropServices;

namespace Govori;

public sealed record InsertionResult(bool Sent, string Status)
{
    public string Message => Localization.T("History." + Status);
}

internal sealed class TextInserter(IDesktopPlatform platform, Func<IClipboard?> clipboardProvider)
{
    private IClipboard Clipboard => clipboardProvider() ?? throw new InvalidOperationException(Localization.T("ClipboardUnavailable"));
    public async Task CopyAsync(string text)
    {
        for (int i = 0; ; i++)
        {
            try { await Clipboard.SetTextAsync(text); await Clipboard.FlushAsync(); return; }
            catch (Exception ex) when (i < 5 && ex is COMException or ExternalException) { await Task.Delay(60); }
        }
    }
    public async Task<InsertionResult> InsertAsync(string text, nint target, bool restore, CancellationToken cancellation)
    {
        if (target == 0 || platform.IsOwnWindow(target)) return new(false, "own_window");
        for (int i = 0; platform.ModifiersDown && i < 100; i++) await Task.Delay(20, cancellation);
        if (platform.ModifiersDown) return new(false, "keys_held");
        if (platform.ForegroundWindow() != target) return new(false, "target_changed");
        DataObject? previous = null;
        if (restore)
        {
            previous = new DataObject();
            // Materialize each format before changing clipboard ownership.
            foreach (var format in await Clipboard.GetFormatsAsync())
            {
                try { var data = await Clipboard.GetDataAsync(format); if (data != null) previous.Set(format, data); }
                catch { }
            }
        }
        await CopyAsync(text);
        uint sequence = OperatingSystem.IsWindows() ? WindowsNative.GetClipboardSequenceNumber() : 0;
        await Task.Delay(80, cancellation);
        if (platform.ForegroundWindow() != target) return new(false, "target_changed_clipboard");
        async Task<bool> Unchanged() => OperatingSystem.IsWindows() ? WindowsNative.GetClipboardSequenceNumber() == sequence : await Clipboard.GetTextAsync() == text;
        if (!await Unchanged()) return new(false, "clipboard_changed");
        platform.Paste();
        await Task.Delay(900, cancellation);
        if (restore && await Unchanged())
        {
            try { if (previous != null && previous.GetDataFormats().Any()) { await Clipboard.SetDataObjectAsync(previous); await Clipboard.FlushAsync(); } else await Clipboard.ClearAsync(); }
            catch { }
        }
        return new(true, "paste_sent");
    }
}
