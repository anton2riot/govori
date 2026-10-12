using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Govori;

internal sealed class MainWindow : Window
{
    private readonly DictationController controller;
    private readonly Grid historyPane = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly ScrollViewer settingsPane = new() { IsVisible = false, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox search = new() { Watermark = Localization.T("Search"), Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel records = new() { Spacing = 10 };
    private readonly TextBlock empty = new() { Text = Localization.T("NoDictations"), Foreground = Brush.Parse("#9393A6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#9393A6"), MaxLines = 2 };
    private readonly Button cancel = new() { Content = Localization.T("Cancel"), IsVisible = false };
    private readonly Button record = new() { Content = Localization.T("Record"), Classes = { "small" } };
    private readonly Button historyNav = new() { Content = Localization.T("History"), Classes = { "nav", "selected" } };
    private readonly Button settingsNav = new() { Content = Localization.T("Settings"), Classes = { "nav" } };
    private readonly HotkeyBox dictation, history;
    private readonly ComboBox microphone = new();
    private readonly TextBox model = new(), vocabulary = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64 };
    private readonly TextBox apiKey = new() { Name = "ApiKey", PasswordChar = '●' };
    private readonly Button saveApiKey = new() { Name = "SaveApiKey", Classes = { "small" } }, deleteApiKey = new() { Name = "DeleteApiKey", Classes = { "small" } };
    private readonly TextBlock apiKeyStatus = new() { Classes = { "hint" }, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox edge = new() { ItemsSource = new[] { Localization.T("Right"), Localization.T("Left") } };
    private readonly ComboBox language = new() { Name = "LanguageSelector" };
    private readonly Dictionary<string, TextBlock> labels = [];
    private readonly TextBlock hint = new() { Classes = { "hint" }, Margin = new Thickness(0, 12, 0, 0) };
    internal sealed record LanguageOption(string Code, string Name);
    private readonly ToggleSwitch restore = new() { OnContent = "", OffContent = "" };
    private readonly ToggleSwitch startup = new() { Name = "StartWithSystem", OnContent = "", OffContent = "" };
    private readonly TextBlock validation = new() { Foreground = Brush.Parse("#BB526D"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private bool loading;

    public MainWindow(DictationController controller)
    {
        this.controller = controller; dictation = new(controller); history = new(controller);
        Title = "Govori"; Width = 660; Height = 580; MinWidth = 600; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Govori.CrossPlatform/Assets/govori.ico")));
        var root = new Grid { RowDefinitions = new RowDefinitions("54,*,Auto") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Govori.CrossPlatform/Assets/govori.png"))), Width = 25, Height = 25 });
        brand.Children.Add(new TextBlock { Text = "Govori", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); header.Children.Add(brand);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        navigation.Children.Add(historyNav); navigation.Children.Add(settingsNav); Grid.SetColumn(navigation, 1); header.Children.Add(navigation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(record); actions.Children.Add(cancel); Grid.SetColumn(actions, 2); header.Children.Add(actions);
        root.Children.Add(new Border { Child = header, Background = Brush.Parse("#FAF9FD"), BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(0, 0, 0, 1) });
        historyPane.Children.Add(search);
        var listArea = new Grid(); listArea.Children.Add(empty); listArea.Children.Add(new ScrollViewer { Content = records, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }); Grid.SetRow(listArea, 1); historyPane.Children.Add(listArea);
        var body = new Grid { Margin = new Thickness(18, 16, 18, 12) }; body.Children.Add(historyPane); body.Children.Add(settingsPane); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Border { Child = status, Padding = new Thickness(18, 8), BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        BuildSettings(); ApplyTranslations();
        Localization.Changed += ApplyTranslations;
        Closed += (_, _) => Localization.Changed -= ApplyTranslations;
        historyNav.Click += (_, _) => ShowPage(false); settingsNav.Click += (_, _) => ShowPage(true);
        cancel.Click += (_, _) => controller.Cancel();
        record.Click += async (_, _) => { if (controller.Recording) await controller.StopRecordingAsync(); else controller.StartRecording(); };
        search.TextChanged += (_, _) => RefreshHistory(); controller.History.Changed += RefreshHistory; controller.SettingsChanged += LoadSettings;
        dictation.ShortcutCommitted += () => Persist(s => s.Hotkey = dictation.Text);
        history.ShortcutCommitted += () => Persist(s => s.HistoryHotkey = history.Text);
        microphone.SelectionChanged += (_, _) => { if (microphone.SelectedItem is Microphone value) Persist(s => s.MicrophoneDevice = value.Id); };
        edge.SelectionChanged += (_, _) => Persist(s => s.IndicatorEdge = edge.SelectedIndex == 1 ? "Left" : "Right");
        restore.IsCheckedChanged += (_, _) => Persist(s => s.RestoreClipboard = restore.IsChecked == true);
        startup.IsCheckedChanged += (_, _) => Persist(s => s.StartWithSystem = startup.IsChecked == true);
        language.SelectionChanged += (_, _) => { if (language.SelectedItem is LanguageOption value) Persist(s => s.Language = value.Code); };
        model.LostFocus += (_, _) => Persist(s => s.Model = model.Text?.Trim() ?? "");
        vocabulary.LostFocus += (_, _) => Persist(s => s.Vocabulary = vocabulary.Text?.Trim() ?? "");
        apiKey.TextChanged += (_, _) => saveApiKey.IsEnabled = !string.IsNullOrWhiteSpace(apiKey.Text);
        saveApiKey.Click += (_, _) =>
        {
            try { controller.SaveApiKey(apiKey.Text ?? ""); apiKey.Text = ""; validation.Text = ""; RefreshApiKeyStatus(); }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        deleteApiKey.Click += (_, _) =>
        {
            try { controller.DeleteApiKey(); apiKey.Text = ""; validation.Text = ""; RefreshApiKeyStatus(); }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        RefreshApiKeyStatus();
    }
    private void BuildSettings()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("155,*"), RowDefinitions = new RowDefinitions("40,40,48,42,76,42,36,36,42,Auto,Auto,Auto") };
        void Row(int row, string title, Control control)
        {
            var label = new TextBlock { Text = Localization.T(title), VerticalAlignment = VerticalAlignment.Center }; labels[title] = label; Grid.SetRow(label, row); grid.Children.Add(label);
            control.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }
        Row(0, "Record", dictation); Row(1, "History", history); Row(2, "Microphone", microphone); Row(3, "Model", model); Row(4, "Vocabulary", vocabulary); Row(5, "Indicator", edge); Row(6, "RestoreClipboard", restore); Row(7, "StartWithSystem", startup); Row(8, "Language", language);
        var keyPane = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 8) };
        var keyLabel = new TextBlock { Text = Localization.T("ApiKey") }; labels["ApiKey"] = keyLabel; keyPane.Children.Add(keyLabel);
        keyPane.Children.Add(apiKey);
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; keyButtons.Children.Add(saveApiKey); keyButtons.Children.Add(deleteApiKey); keyPane.Children.Add(keyButtons);
        keyPane.Children.Add(apiKeyStatus); Grid.SetRow(keyPane, 9); Grid.SetColumnSpan(keyPane, 2); grid.Children.Add(keyPane);
        Grid.SetRow(validation, 10); Grid.SetColumnSpan(validation, 2); grid.Children.Add(validation);
        Grid.SetRow(hint, 11); Grid.SetColumnSpan(hint, 2); grid.Children.Add(hint); settingsPane.Content = grid;
        microphone.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Microphone>((value, _) => new TextBlock { Text = value?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        language.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LanguageOption>((value, _) => new TextBlock { Text = value?.Name });
    }
    private void ApplyTranslations()
    {
        loading = true;
        try
        {
            search.Watermark = Localization.T("Search"); cancel.Content = Localization.T("Cancel");
            record.Content = Localization.T(controller.Recording ? "Stop" : "Record");
            historyNav.Content = Localization.T("History"); settingsNav.Content = Localization.T("Settings");
            apiKey.Watermark = Localization.T("ApiKeyPlaceholder"); saveApiKey.Content = Localization.T("SaveApiKey"); deleteApiKey.Content = Localization.T("DeleteApiKey");
            foreach (var pair in labels) pair.Value.Text = Localization.T(pair.Key);
            edge.ItemsSource = new[] { Localization.T("Right"), Localization.T("Left") };
            language.ItemsSource = new[] { new LanguageOption("system", Localization.T("SystemLanguage")), new LanguageOption("en", Localization.T("LanguageEnglish")), new LanguageOption("ru", Localization.T("LanguageRussian")) };
            hint.Text = controller.Platform.Limitation ?? Localization.T("RecordingHint");
            validation.Text = Localization.TranslateKnown(validation.Text ?? "");
            status.Text = Localization.TranslateKnown(status.Text ?? "");
        }
        finally { loading = false; }
        LoadSettings(); RefreshHistory(); RefreshApiKeyStatus();
    }
    private void RefreshApiKeyStatus()
    {
        bool configured = !string.IsNullOrWhiteSpace(Configuration.ApiKey);
        apiKeyStatus.Text = Configuration.ApiKeyStorageError ?? Localization.T(Configuration.HasSavedApiKey ? "ApiKeySaved" : configured ? "ApiKeyFromEnvironment" : "ApiKeyMissing");
        deleteApiKey.IsEnabled = Configuration.HasSavedApiKey;
        saveApiKey.IsEnabled = !string.IsNullOrWhiteSpace(apiKey.Text);
    }
    private void LoadSettings()
    {
        loading = true;
        try
        {
            var s = controller.Settings; dictation.Text = s.Hotkey; history.Text = s.HistoryHotkey; model.Text = s.Model; vocabulary.Text = s.Vocabulary;
            restore.IsChecked = s.RestoreClipboard; startup.IsChecked = s.StartWithSystem; edge.SelectedIndex = s.IndicatorEdge == "Left" ? 1 : 0;
            language.SelectedItem = ((IEnumerable<LanguageOption>)language.ItemsSource!).First(x => x.Code == s.Language);
            var devices = controller.Recorder.Devices(); microphone.ItemsSource = devices; microphone.SelectedItem = devices.FirstOrDefault(x => x.Id == s.MicrophoneDevice) ?? devices[0];
        }
        finally { loading = false; }
    }
    private void Persist(Action<Settings> change)
    {
        if (loading) return;
        var next = controller.Settings.Clone(); change(next);
        try { controller.CommitSettings(next); validation.Text = ""; }
        catch (Exception ex) { validation.Text = ex.Message; LoadSettings(); }
    }
    private void ShowPage(bool settings)
    {
        historyPane.IsVisible = !settings; settingsPane.IsVisible = settings;
        historyNav.Classes.Set("selected", !settings); settingsNav.Classes.Set("selected", settings);
        if (settings) RefreshApiKeyStatus();
    }
    public void OpenHistory() { ShowPage(false); Show(); WindowState = WindowState.Normal; Activate(); RefreshHistory(); }
    public void OpenSettings() { ShowPage(true); Show(); WindowState = WindowState.Normal; Activate(); }
    public void UpdateStatus(string? text, bool busy, bool recording)
    {
        if (text != null) status.Text = text; cancel.IsVisible = busy; record.Content = recording ? Localization.T("Stop") : Localization.T("Record");
    }
    public void RefreshHistory()
    {
        var query = search.Text ?? "";
        var entries = controller.History.Entries.Where(x => query.Length == 0 || x.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Target.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        records.Children.Clear(); empty.IsVisible = entries.Count == 0; empty.Text = query.Length == 0 ? Localization.T("NoDictations") : Localization.T("NoMatches");
        foreach (var entry in entries)
        {
            var card = new StackPanel { Spacing = 6 };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(new TextBlock { Text = entry.TimeLabel, FontSize = 11, Foreground = Brush.Parse("#9393A6"), VerticalAlignment = VerticalAlignment.Center });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            var copy = new Button { Content = Localization.T("Copy"), Classes = { "small" }, IsEnabled = entry.Text.Length > 0 };
            copy.Click += async (_, _) => { try { await controller.Inserter.CopyAsync(entry.Text); } catch (Exception ex) { controller.SetStatus(ex.Message); } }; buttons.Children.Add(copy);
            if (entry.CanRetry) { var retry = new Button { Content = Localization.T("Retry"), Classes = { "small" }, IsEnabled = !controller.IsProcessing(entry.Id) }; retry.Click += (_, _) => controller.Retry(entry); buttons.Children.Add(retry); }
            var delete = new Button { Content = Localization.T("Delete"), Classes = { "small" }, IsEnabled = !controller.IsProcessing(entry.Id) };
            delete.Click += (_, _) => { if (!controller.IsProcessing(entry.Id)) { try { controller.History.Delete(entry); } catch (Exception ex) { controller.SetStatus(ex.Message); } } }; buttons.Children.Add(delete);
            Grid.SetColumn(buttons, 1); top.Children.Add(buttons); card.Children.Add(top);
            card.Children.Add(new TextBox { Text = entry.DisplayText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MaxHeight = 170 });
            if (entry.Error.Length > 0) card.Children.Add(new TextBlock { Text = entry.StatusLabel, Classes = { "hint" } });
            records.Children.Add(new Border { Child = card, BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9) });
        }
    }
}
