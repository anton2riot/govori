using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Globalization;

namespace Govori;

internal static class Verification
{
    private static readonly List<string> checks = [];
    private static readonly List<object> audioTimings = [];
    private static string Report => Path.Combine(Configuration.DataDirectory, "verification.txt");
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        checks.Add("PASS: " + description); File.WriteAllLines(Report, checks);
    }
    internal static void WriteFailure(Exception ex)
    {
        checks.Add(ex.ToString()); File.WriteAllLines(Report, checks);
    }
    public static Window CreatePasteTarget(string ready, string output)
    {
        var text = new TextBox { AcceptsReturn = true, Watermark = "Проверка вставки" };
        var window = new Window { Title = "Govori CrossPlatform paste test", Width = 480, Height = 180, Position = new PixelPoint(80, 80), Content = text };
        text.TextChanged += (_, _) => File.WriteAllText(output, text.Text ?? "");
        window.Activated += (_, _) => text.Focus();
        window.Opened += async (_, _) => { await Task.Delay(150); text.Focus(); File.WriteAllText(ready, (window.TryGetPlatformHandle()?.Handle ?? 0).ToInt64().ToString()); };
        return window;
    }
    public static async Task RunAsync(MainWindow window, DictationController controller, string[] args)
    {
        checks.Clear(); audioTimings.Clear(); await Task.Delay(300);
        Check(Configuration.DataDirectory.Contains("CrossPlatform", StringComparison.OrdinalIgnoreCase) || args.Contains("--data-dir"), "separate experiment data directory");
        Check(Hotkey.Parse("ctrl+space").Label == "Ctrl+Space" && Hotkey.Parse("RightAlt").Key == 0xA5, "hotkeys parse and normalize");
        bool rejected = false; try { Hotkey.Parse("Space"); } catch (FormatException) { rejected = true; }
        Check(rejected, "unmodified Space cannot become a global shortcut");
        var settings = controller.Settings.Clone(); string originalModel = settings.Model;
        settings.Model = "invalid-model"; rejected = false;
        try { controller.CommitSettings(settings); } catch (FormatException) { rejected = true; }
        Check(rejected && controller.Settings.Model == originalModel, "invalid settings leave applied settings intact");
        if (args.Contains("--tray"))
        {
            Check(!window.IsVisible, "tray startup hides the main window once");
            var app = (App)Application.Current!;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                app.OpenFromTray(settings: cycle == 1); await Task.Delay(100);
                Check(window.IsVisible && window.WindowState == WindowState.Normal, $"tray open keeps the main window visible, cycle {cycle + 1}");
                window.Hide();
            }
            app.OpenFromTray(); await Task.Delay(100);
        }
        if (OperatingSystem.IsWindows()) await StartupChecks(window, controller);
        await ApiKeyChecks(window);
        await LanguageChecks(window, controller);
        await HotkeySettingsChecks(window, controller);
        settings = controller.Settings.Clone(); settings.Vocabulary = "Gemini, Unity, 42"; controller.CommitSettings(settings);
        Check(Configuration.LoadSettings().Vocabulary == settings.Vocabulary, "settings persist in experiment directory");
        Check(GeminiClient.ParseResponse("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"thought\":true,\"text\":\"hidden\"},{\"text\":\"Привет, Unity!\"}]}}]}") == "Привет, Unity!", "Gemini response excludes reasoning");
        string fakeAudio = Path.Combine(Configuration.AudioDirectory, "retry-test.wav"); File.WriteAllBytes(fakeAudio, [1, 2, 3]);
        var retry = new HistoryEntry { AudioPath = fakeAudio, Status = "transcribing" }; controller.History.Add(retry);
        Check(new HistoryStore().Entries[0].CanRetry && new HistoryStore().Entries[0].Status == "interrupted", "failed or interrupted recordings survive restart");
        controller.History.Delete(retry); Check(!File.Exists(fakeAudio), "deleting history cleans its recording");
        using (var fake = new GeminiClient(new FailedApi()))
        {
            string failureAudio = Path.Combine(Configuration.AudioDirectory, "failed-api.wav"); File.WriteAllBytes(failureAudio, [1,2,3]);
            rejected = false; try { await fake.TranscribeAsync(failureAudio, settings, "test-key", CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && File.Exists(failureAudio), "API rejection preserves audio for retry"); File.Delete(failureAudio);
        }
        controller.History.Add(new HistoryEntry { Text = "Проверка диктовки: Gemini, Unity, 42!", Status = "saved", Duration = 3.2 });
        Render(window, Path.Combine(Configuration.DataDirectory, "history.png"));
        window.OpenSettings(); await Task.Delay(200); Render(window, Path.Combine(Configuration.DataDirectory, "settings.png"));
        window.OpenHistory();
        Check(controller.Recorder.Devices().Count > 1, "PortAudio enumerates input devices");
        int readyNotifications = 0;
        void OnReady() => Interlocked.Increment(ref readyNotifications);
        controller.Recorder.BecameReady += OnReady;
        var microphoneWait = Stopwatch.StartNew();
        controller.Recorder.Start(-1);
        while (!controller.Recorder.IsReady && controller.Recorder.IsRecording && microphoneWait.Elapsed.TotalSeconds < 5) await Task.Delay(40);
        Check(controller.Recorder.IsReady, "microphone readiness follows received audio");
        double readyMs = microphoneWait.Elapsed.TotalMilliseconds;
        await Task.Delay(400);
        Check(Volatile.Read(ref readyNotifications) == 1, "microphone notifies readiness once without waiting for UI polling");
        var microphoneStop = Stopwatch.StartNew();
        var recording = (await controller.Recorder.StopAsync())!;
        controller.Recorder.BecameReady -= OnReady;
        double stopMs = microphoneStop.Elapsed.TotalMilliseconds;
        var wav = File.ReadAllBytes(recording.Path);
        Check(recording.Duration > 0.2 && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8) && BitConverter.ToInt32(wav, 40) == wav.Length - 44, "real microphone records a complete PCM WAV");
        Check(BitConverter.ToInt16(wav, 22) == 1 && BitConverter.ToInt16(wav, 34) == 16 && Math.Abs(recording.Duration - (wav.Length - 44) / (double)BitConverter.ToInt32(wav, 28)) < 0.001, "speech WAV is mono PCM16 with accurate duration");
        audioTimings.Add(new { stage = "microphone", readyMs, stopMs, sampleRate = BitConverter.ToInt32(wav, 24), bytesPerSecond = BitConverter.ToInt32(wav, 28) });
        Check(!controller.Recorder.IsRecording && !controller.Recorder.IsReady, "microphone stops cleanly"); File.Delete(recording.Path);
        for (int i = 0; i < 3; i++)
        {
            controller.Recorder.Start(-1); await Task.Delay(100);
            var shortRecording = (await controller.Recorder.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            Check(!controller.Recorder.IsRecording && File.ReadAllBytes(shortRecording.Path).AsSpan(0, 4).SequenceEqual("RIFF"u8), $"microphone reopens and stops promptly, cycle {i + 1}");
            File.Delete(shortRecording.Path);
        }
        if (controller.Hotkeys is HotkeyState realHook)
        {
            int beforeRecording = controller.History.Entries.Count;
            var shortcut = controller.Hotkeys.Dictation;
            var readyWait = Stopwatch.StartNew();
            Check(realHook.HandleKey(shortcut.Key, true, false, false, true), "dictation shortcut is handled by the live controller hook");
            while (!controller.RecordingReady && readyWait.Elapsed.TotalSeconds < 5) await Task.Delay(40);
            Check(controller.RecordingReady && controller.Indicator?.State == "recording", "hotkey reaches the actual ready state and green recording indicator");
            var readySound = controller.ReadySoundPlayback;
            Check(readySound != null && await readySound.WaitAsync(TimeSpan.FromSeconds(5)), "microphone readiness plays the sound through the default output device");
            await Task.Delay(100);
            Check(ReferenceEquals(controller.ReadySoundPlayback, readySound), "readiness sound plays only once per recording despite timer updates");
            audioTimings.Add(new { stage = "hotkey-to-ready", readyMs = readyWait.Elapsed.TotalMilliseconds });
            Render(controller.Indicator!, Path.Combine(Configuration.DataDirectory, "indicator-ready.png"));
            controller.Cancel(); realHook.HandleKey(shortcut.Key, false, true, false);
            var stopWait = Stopwatch.StartNew(); while (controller.Busy && stopWait.Elapsed.TotalSeconds < 5) await Task.Delay(40);
            Check(!controller.Busy && controller.Indicator?.IsVisible == false && controller.History.Entries.Count == beforeRecording, "cancelling hotkey recording stops capture and dismisses the indicator");
        }
        if (OperatingSystem.IsWindows()) await WindowsChecks(window, controller);
        if (args.Contains("--live-api"))
        {
            int index = Array.IndexOf(args, "--live-api"); string audio = args[index + 1];
            string copy = Path.Combine(Configuration.AudioDirectory, "synthetic-speech.wav"); File.Copy(audio, copy, true);
            var entry = new HistoryEntry { AudioPath = copy, Duration = 3 }; controller.History.Add(entry);
            await controller.ProcessAsync(entry, 0, false);
            Check(entry.Text.Contains("42") && (entry.Text.Contains("диктовки", StringComparison.OrdinalIgnoreCase) || entry.Text.Contains("dictation", StringComparison.OrdinalIgnoreCase)), "synthetic speech transcribes through live Gemini");
            Check(!File.Exists(copy) && entry.AudioPath.Length == 0, "successful transcription cleans its audio");
        }
        controller.History.Add(new HistoryEntry { Text = "Можно проверить новую версию на Windows: удерживайте F8 и говорите.", Duration = 4 });
        window.OpenHistory(); await Task.Delay(150); Render(window, Path.Combine(Configuration.DataDirectory, "history.png"));
        checks.Add($"Completed: {checks.Count} checks"); File.WriteAllLines(Report, checks);
        File.WriteAllText(Path.Combine(Configuration.DataDirectory, "audio-timings.json"), JsonSerializer.Serialize(audioTimings, new JsonSerializerOptions { WriteIndented = true }));
    }
    [SupportedOSPlatform("windows")]
    private static async Task StartupChecks(MainWindow window, DictationController controller)
    {
        Check(Configuration.DataDirectory != Configuration.DefaultDataDirectory, "startup checks use an isolated directory");
        window.OpenSettings(); await Task.Delay(100);
        var checkbox = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(x => x.Name == "StartWithSystem");
        Check(checkbox.IsVisible && checkbox.Bounds.Height > 0, "startup flag is visible in settings");
        string shortcutPath = Path.Combine(Configuration.DataDirectory, "startup", WindowsStartup.ShortcutName);
        checkbox.IsChecked = false;
        checkbox.IsChecked = true;
        Check(controller.Settings.StartWithSystem && Configuration.LoadSettings().StartWithSystem && File.Exists(shortcutPath), "enabling startup from settings saves the flag and creates a shortcut");
        var shortcut = WindowsStartup.Read(shortcutPath);
        Check(shortcut.Target == Environment.ProcessPath && shortcut.Arguments == "--tray"
            && shortcut.WorkingDirectory == Path.GetDirectoryName(Environment.ProcessPath), "startup shortcut targets the executable with tray mode and its working directory");
        DateTime unchanged = File.GetLastWriteTimeUtc(shortcutPath);
        controller.Platform.ConfigureStartup(true);
        Check(File.GetLastWriteTimeUtc(shortcutPath) == unchanged, "reconciling enabled startup preserves a correct shortcut");
        File.Delete(shortcutPath);
        controller.Platform.ConfigureStartup(true);
        Check(File.Exists(shortcutPath), "enabled startup repairs a missing shortcut");
        checkbox.IsChecked = false;
        Check(!controller.Settings.StartWithSystem && !Configuration.LoadSettings().StartWithSystem && !File.Exists(shortcutPath), "disabling startup from settings removes the shortcut and saves the flag");
        window.OpenHistory();
    }

    private static async Task ApiKeyChecks(MainWindow window)
    {
        Check(Configuration.DataDirectory != Configuration.DefaultDataDirectory, "API key checks use an isolated directory");
        string? previousEnvironment = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        byte[]? previousSaved = File.Exists(ApiKeyStore.FilePath) ? File.ReadAllBytes(ApiKeyStore.FilePath) : null;
        window.OpenSettings(); await Task.Delay(100);
        var field = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "ApiKey");
        var save = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SaveApiKey");
        var delete = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "DeleteApiKey");
        try
        {
            ApiKeyStore.Delete();
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "verification-environment-key");
            Check(field.PasswordChar != default && !save.IsEnabled, "API key input is masked and empty keys cannot be saved");
            field.Text = "verification-saved-key"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Configuration.LoadEnvironment();
            Check(ApiKeyStore.Read() == "verification-saved-key" && Configuration.ApiKey == "verification-saved-key", "key saved through the interface persists and overrides the environment immediately");
            Check(string.IsNullOrEmpty(field.Text) && delete.IsEnabled, "saving clears the key input and enables deletion");
            Check(!File.ReadAllText(Configuration.SettingsPath).Contains("verification-saved-key"), "API key is kept out of settings JSON");
            if (OperatingSystem.IsWindows())
                Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(ApiKeyStore.FilePath)).Contains("verification-saved-key"), "Windows stores the API key encrypted");
            field.Text = "invalid key"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ApiKeyStore.Read() == "verification-saved-key", "invalid key input preserves the saved key");
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!File.Exists(ApiKeyStore.FilePath) && Configuration.ApiKey == "verification-environment-key" && !delete.IsEnabled, "deleting the saved key restores the environment fallback");
            save.BringIntoView(); await Task.Delay(100);
            Render(window, Path.Combine(Configuration.DataDirectory, "settings-api-key.png"));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllBytes(ApiKeyStore.FilePath, [1, 2, 3]);
                Check(Configuration.ApiKey == "verification-environment-key" && Configuration.ApiKeyStorageError != null, "an unreadable saved key reports an error without blocking the environment fallback");
            }
        }
        finally
        {
            ApiKeyStore.Delete();
            if (previousSaved != null) File.WriteAllBytes(ApiKeyStore.FilePath, previousSaved);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousEnvironment);
            field.Text = "";
            window.OpenHistory(); window.OpenSettings();
            field.GetVisualAncestors().OfType<ScrollViewer>().First().Offset = default;
        }
    }

    private static async Task LanguageChecks(MainWindow window, DictationController controller)
    {
        Check(new Settings().Language == "system", "new installations follow the system language");
        Check(Localization.ResolveLanguage("system", CultureInfo.GetCultureInfo("ru-RU")) == "ru"
            && Localization.ResolveLanguage("system", CultureInfo.GetCultureInfo("en-US")) == "en"
            && Localization.ResolveLanguage("system", CultureInfo.GetCultureInfo("de-DE")) == "en", "system language resolution includes regional variants and English fallback");
        Check(Localization.Catalog("en").Keys.Order().SequenceEqual(Localization.Catalog("ru").Keys.Order()), "English and Russian catalogs contain the same keys");
        Check(Localization.Catalog("en").Keys.All(key => System.Text.CompositeFormat.Parse(Localization.Catalog("en")[key]).MinimumArgumentCount
            == System.Text.CompositeFormat.Parse(Localization.Catalog("ru")[key]).MinimumArgumentCount), "all translation formatting placeholders match");
        var original = controller.Settings.Clone();
        try
        {
            window.OpenSettings(); await Task.Delay(50);
            var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "LanguageSelector");
            foreach (var code in new[] { "en", "ru", "system" })
            {
                selector.SelectedItem = ((IEnumerable<MainWindow.LanguageOption>)selector.ItemsSource!).Single(x => x.Code == code);
                await Task.Delay(50);
                Check(controller.Settings.Language == code && Configuration.LoadSettings().Language == code, "language selector applies and persists " + code);
                Check(window.GetVisualDescendants().OfType<Button>().Any(x => Equals(x.Content, Localization.T("Settings"))), "navigation updates without restart: " + code);
                Render(window, Path.Combine(Configuration.DataDirectory, "settings-" + code + ".png"));
            }
            var invalid = controller.Settings.Clone(); invalid.Language = "invalid";
            bool rejected = false; try { controller.CommitSettings(invalid); } catch (FormatException) { rejected = true; }
            Check(rejected && controller.Settings.Language == "system", "invalid language leaves settings intact");
            var legacyPath = Path.Combine(Configuration.DataDirectory, "legacy-history.json");
            File.WriteAllText(legacyPath, JsonSerializer.Serialize(new[] { new HistoryEntry { Status = Localization.Catalog("ru")["History.transcribing"] }, new HistoryEntry { Status = Localization.Catalog("en")["History.paste_sent"], Text = "Legacy text" } }));
            var migrated = new HistoryStore(legacyPath);
            Check(migrated.Entries[0].Status == "interrupted" && migrated.Entries[1].Status == "paste_sent" && migrated.Entries[1].Text == "Legacy text", "legacy history statuses migrate without changing dictated text");
            Localization.Apply("en"); var entry = new HistoryEntry { Status = "saved" }; var englishLabel = entry.StatusLabel;
            Localization.Apply("ru"); Check(entry.Status == "saved" && entry.StatusLabel != englishLabel, "history status labels follow language changes without rewriting status codes");
        }
        finally { controller.CommitSettings(original); Localization.Apply(original.Language); }
    }
    private static async Task HotkeySettingsChecks(MainWindow window, DictationController controller)
    {
        var original = controller.Settings.Clone();
        window.OpenSettings(); await Task.Delay(100);
        var boxes = window.GetVisualDescendants().OfType<HotkeyBox>().ToArray();
        Check(boxes.Length == 2, "settings expose both live shortcut controls");
        var dictation = boxes[0]; var history = boxes[1];
        void Begin(HotkeyBox box) => box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Key(HotkeyBox box, Avalonia.Input.Key key, bool up = false, KeyModifiers modifiers = KeyModifiers.None)
            => box.RaiseEvent(new KeyEventArgs { RoutedEvent = up ? InputElement.KeyUpEvent : InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        try
        {
            Begin(dictation);
            Check(dictation.IsCapturing && !controller.Hotkeys.Enabled, "capturing a shortcut suspends global hotkeys");
            Key(dictation, Avalonia.Input.Key.LeftCtrl);
            Key(dictation, Avalonia.Input.Key.Space, modifiers: KeyModifiers.Control);
            Key(dictation, Avalonia.Input.Key.Space, modifiers: KeyModifiers.Control); // Repeat while held.
            Key(dictation, Avalonia.Input.Key.Space, up: true, modifiers: KeyModifiers.Control);
            Key(dictation, Avalonia.Input.Key.LeftCtrl, up: true); await Task.Delay(50);
            Check(!dictation.IsCapturing && Equals(dictation.Content, "Ctrl  +  Space") && controller.Hotkeys.Enabled, "Ctrl+Space release and repeats do not reopen shortcut capture");
            Check(controller.Settings.Hotkey == "Ctrl+Space" && Configuration.LoadSettings().Hotkey == "Ctrl+Space", "captured dictation shortcut is applied and survives reload");
            Begin(history);
            Key(history, Avalonia.Input.Key.LeftCtrl); Key(history, Avalonia.Input.Key.LeftAlt, modifiers: KeyModifiers.Control);
            Key(history, Avalonia.Input.Key.Space, modifiers: KeyModifiers.Control | KeyModifiers.Alt);
            Key(history, Avalonia.Input.Key.Space, up: true, modifiers: KeyModifiers.Control | KeyModifiers.Alt);
            Key(history, Avalonia.Input.Key.LeftAlt, up: true); Key(history, Avalonia.Input.Key.LeftCtrl, up: true); await Task.Delay(50);
            Check(!history.IsCapturing && controller.Settings.HistoryHotkey == "Ctrl+Alt+Space" && Configuration.LoadSettings().HistoryHotkey == "Ctrl+Alt+Space" && controller.Hotkeys.Enabled, "history shortcut capture also saves and restores global hotkeys");
            Begin(dictation); Key(dictation, Avalonia.Input.Key.RightAlt); Key(dictation, Avalonia.Input.Key.RightAlt, up: true); await Task.Delay(50);
            Check(!dictation.IsCapturing && controller.Settings.Hotkey == "RightAlt" && controller.Hotkeys.Enabled, "standalone RightAlt commits on release");
            Begin(dictation); Key(dictation, Avalonia.Input.Key.Space); Key(dictation, Avalonia.Input.Key.Space, up: true);
            Check(dictation.IsCapturing && controller.Settings.Hotkey == "RightAlt", "invalid unmodified Space leaves the previous shortcut intact");
            Key(dictation, Avalonia.Input.Key.Escape); Key(dictation, Avalonia.Input.Key.Escape, up: true); await Task.Delay(50);
            Check(!dictation.IsCapturing && controller.Hotkeys.Enabled, "Escape cancels capture and resumes shortcuts");
            controller.Hotkeys.Enabled = false;
            Begin(dictation); Key(dictation, Avalonia.Input.Key.F9); Key(dictation, Avalonia.Input.Key.F9, up: true); await Task.Delay(50);
            await controller.SetHotkeyCapture(false).WaitAsync(TimeSpan.FromSeconds(5));
            Check(!dictation.IsCapturing && !controller.Hotkeys.Enabled, "shortcut capture preserves a user-paused state");
            controller.Hotkeys.Enabled = true;
            await controller.SetHotkeyCapture(true); await controller.SetHotkeyCapture(true); await controller.SetHotkeyCapture(false).WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.Hotkeys.Enabled, "repeated capture requests preserve the initial global hotkey state");
        }
        finally { await controller.SetHotkeyCapture(false).WaitAsync(TimeSpan.FromSeconds(5)); controller.Hotkeys.Enabled = true; controller.CommitSettings(original); }
        window.OpenHistory();
    }
    private static async Task WindowsChecks(MainWindow window, DictationController controller)
    {
        using (var hook = new WindowsHotkeys(controller.Platform, Hotkey.Parse("F10"), Hotkey.Parse("F11")))
        {
            int presses = 0, releases = 0, cancels = 0, histories = 0;
            hook.Pressed += () => presses++; hook.Released += () => releases++; hook.Cancelled += () => cancels++; hook.HistoryRequested += () => histories++;
            hook.HandleKey(0x79, true, false, false, true); hook.HandleKey(0x79, true, false, false, true); hook.HandleKey(0x79, false, true, false);
            Check(presses == 1 && releases == 1, "native hook starts once and stops on key release");
            hook.HandleKey(0x79, true, false, true, true); Check(presses == 1, "injected input does not start recording");
            hook.Recording = true; hook.HandleKey(0x1B, true, false, false); hook.HandleKey(0x1B, false, true, false); Check(cancels == 1, "Escape cancels recording");
            hook.Recording = false; hook.HandleKey(0x7A, true, false, false, true); hook.HandleKey(0x7A, false, true, false); Check(histories == 1, "history hotkey dispatches");
            hook.Dictation = Hotkey.Parse("Ctrl+Space"); hook.HandleKey(0x20, true, false, false, true); hook.HandleKey(0xA2, false, true, false); hook.HandleKey(0x20, false, true, false);
            Check(presses == 2 && releases == 2, "releasing a modifier also ends a held chord");
            hook.Enabled = false; Check(!hook.HandleKey(0x20, true, false, false, true), "paused hotkeys pass through");
        }
        var userClipboard = new DataObject();
        foreach (var format in await window.Clipboard!.GetFormatsAsync()) { try { var value = await window.Clipboard.GetDataAsync(format); if (value != null) userClipboard.Set(format, value); } catch { } }
        string ready = Path.Combine(Configuration.DataDirectory, "paste-ready.txt"), output = Path.Combine(Configuration.DataDirectory, "paste-output.txt");
        if (File.Exists(ready)) File.Delete(ready); if (File.Exists(output)) File.Delete(output);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--paste-target"); start.ArgumentList.Add(ready); start.ArgumentList.Add(output);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Configuration.DataDirectory);
        using var child = Process.Start(start)!;
        try
        {
            window.Hide(); var wait = Stopwatch.StartNew();
            while (!File.Exists(ready) && wait.Elapsed.TotalSeconds < 10 && !child.HasExited) await Task.Delay(50);
            Check(File.Exists(ready), "external Avalonia input window launches");
            nint target = (nint)long.Parse(File.ReadAllText(ready));
            for (int attempt = 0; attempt < 20 && controller.Platform.ForegroundWindow() != target; attempt++) { FocusTarget(target); await Task.Delay(80); }
            Check(controller.Platform.ForegroundWindow() == target, $"external input field receives focus (target={target}, foreground={controller.Platform.ForegroundWindow()})");
            using (var indicator = new OverlayScope(controller.Platform))
            {
                indicator.Window.ShowState(target, "Right", "recording"); await Task.Delay(200);
                WindowsPlatform.GetWindowRect(indicator.Window.TryGetPlatformHandle()!.Handle, out var indicatorRect);
                File.WriteAllText(Path.Combine(Configuration.DataDirectory, "indicator-layout.json"), JsonSerializer.Serialize(new { indicator.Window.Bounds, indicator.Window.ClientSize, indicator.Window.RenderScaling, NativeWidth = indicatorRect.Right - indicatorRect.Left, NativeHeight = indicatorRect.Bottom - indicatorRect.Top }, new JsonSerializerOptions { WriteIndented = true }));
                Check(indicatorRect.Right - indicatorRect.Left == 18 && indicatorRect.Bottom - indicatorRect.Top == 18, "indicator native window is exactly 18 by 18 physical pixels at current DPI");
                Render(indicator.Window, Path.Combine(Configuration.DataDirectory, "indicator.png"));
                Check(controller.Platform.ForegroundWindow() == target, "recording indicator does not steal external input focus");
            }
            var before = new DataObject(); before.Set(DataFormats.Text, "original clipboard"); before.Set("HTML Format", "<b>clipboard-format-test</b>"); await window.Clipboard.SetDataObjectAsync(before);
            var result = await controller.Inserter.InsertAsync("Проверка: Gemini, Unity, 42!", target, true, CancellationToken.None);
            Check(result.Sent && File.Exists(output) && File.ReadAllText(output).Contains("Проверка: Gemini, Unity, 42!", StringComparison.Ordinal), $"Unicode text reaches an external field through native Ctrl+V (sent={result.Sent}, status={result.Message}, target={target}, foreground={controller.Platform.ForegroundWindow()}, output={File.Exists(output)})");
            Check(await window.Clipboard.GetTextAsync() == "original clipboard", "clipboard text is restored after paste");
            Check((await window.Clipboard.GetDataAsync("HTML Format")) is not null, "clipboard non-text format is restored");
            result = await controller.Inserter.InsertAsync("must not paste", (nint)1, false, CancellationToken.None);
            Check(!result.Sent && !File.ReadAllText(output).Contains("must not paste", StringComparison.Ordinal), "changed target prevents stray insertion");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            if (userClipboard.GetDataFormats().Any()) { await window.Clipboard.SetDataObjectAsync(userClipboard); await window.Clipboard.FlushAsync(); } else await window.Clipboard.ClearAsync();
            window.Show();
        }
    }
    internal static void Render(Window window, string path)
    {
        double scaling = window.RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Round(window.Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(window.Bounds.Height * scaling)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling)); bitmap.Render(window); bitmap.Save(path);
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    private static void FocusTarget(nint target)
    {
        var foreground = WindowsNative.GetForegroundWindow();
        uint current = GetCurrentThreadId(), other = WindowsNative.GetWindowThreadProcessId(foreground, out _);
        bool attached = current != other && other != 0 && AttachThreadInput(current, other, true);
        try { ShowWindow(target, 5); SetWindowPos(target, (nint)(-1), 0, 0, 0, 0, 3); BringWindowToTop(target); SetForegroundWindow(target); }
        finally { if (attached) AttachThreadInput(current, other, false); }
    }
    private sealed class FailedApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }
    private sealed class OverlayScope(IDesktopPlatform platform) : IDisposable
    {
        internal OverlayWindow Window { get; } = new(platform);
        public void Dispose() => Window.Close();
    }
}
