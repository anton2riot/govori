using Avalonia;
using Avalonia.Automation;
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
    private readonly ScrollViewer settingsPane = PageScroll("SettingsScroll");
    private readonly Grid spendingPane = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly StackPanel dailySpending = new() { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
    private readonly Grid spendingTotals = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
    private readonly TabControl tabs = new() { Name = "MainTabs", Padding = new Thickness(18, 12, 18, 16) };
    private readonly TextBox search = new() { Name = "HistorySearch", Margin = new Thickness(0, 0, 0, 12), InnerLeftContent = new StandardIcon("search") { Margin = new Thickness(10, 0, 4, 0), Foreground = Brush.Parse("#9393A6") } };
    private readonly StackPanel records = new() { Spacing = 10 };
    private readonly TextBlock empty = new() { Text = Localization.T("NoDictations"), Foreground = Brush.Parse("#9393A6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { Name = "ApplicationStatus", TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = Brush.Parse("#9393A6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0) };
    private readonly Button cancel = new() { Name = "CancelDictation", IsVisible = false };
    private readonly Button record = new() { Name = "RecordDictation", Classes = { "small" } };
    private readonly TabItem historyNav = new() { Name = "HistoryNavigation" };
    private readonly TabItem spendingNav = new() { Name = "SpendingNavigation" };
    private readonly TabItem settingsNav = new() { Name = "SettingsNavigation" };
    private readonly HotkeyBox dictation, history;
    private readonly ComboBox microphone = new();
    private readonly ComboBox provider = new() { Name = "ProviderSelector" }, model = new() { Name = "ModelSelector" };
    private readonly TextBox serverUrl = new() { Name = "LiteLlmServerUrl" };
    private readonly TextBox vocabulary = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 56 };
    private IReadOnlyList<AudioModel> audioModels = TranscriptionClient.GeminiModels;
    private string modelsProvider = Providers.Gemini;
    private int modelsVersion;
    private CancellationTokenSource? modelLoad;
    private CancellationTokenSource? keySave, vocabularySave, urlSave;
    internal string? ModelLoadError { get; private set; }
    private readonly TextBox apiKey = new() { Name = "ApiKey", PasswordChar = '●' };
    private readonly Button deleteApiKey = new() { Name = "DeleteApiKey", Classes = { "small" } };
    private readonly ComboBox edge = new() { ItemsSource = new[] { Localization.T("Right"), Localization.T("Left") } };
    private readonly ComboBox language = new() { Name = "LanguageSelector" };
    private readonly Dictionary<string, TextBlock> labels = [];
    internal sealed record LanguageOption(string Code, string Name);
    internal sealed record ProviderOption(string Code, string Name) { public override string ToString() => Name; }
    private readonly ToggleSwitch restore = new() { OnContent = "", OffContent = "" };
    private readonly ToggleSwitch startup = new() { Name = "StartWithSystem", OnContent = "", OffContent = "" };
    private readonly TextBlock validation = new() { Foreground = Brush.Parse("#BB526D"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private bool loading;
    internal string? StatusText => status.Text;

    public MainWindow(DictationController controller)
    {
        this.controller = controller; dictation = new(controller); history = new(controller);
        Title = "Govori"; Width = 660; Height = 580; MinWidth = 600; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Govori.CrossPlatform/Assets/govori.ico")));
        var root = new Grid { RowDefinitions = new RowDefinitions("48,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(18, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Govori.CrossPlatform/Assets/govori.png"))), Width = 25, Height = 25 });
        brand.Children.Add(new TextBlock { Text = "Govori", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }); header.Children.Add(brand);
        Grid.SetColumn(status, 1); header.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(record); actions.Children.Add(cancel); Grid.SetColumn(actions, 2); header.Children.Add(actions);
        root.Children.Add(new Border { Child = header, Background = Brush.Parse("#FAF9FD"), BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(0, 0, 0, 1) });
        historyPane.Children.Add(search);
        var listArea = new Grid(); listArea.Children.Add(empty); var historyScroll = PageScroll("HistoryScroll"); historyScroll.Content = records; listArea.Children.Add(historyScroll); Grid.SetRow(listArea, 1); historyPane.Children.Add(listArea);
        BuildSpending();
        historyNav.Content = historyPane; spendingNav.Content = spendingPane; settingsNav.Content = settingsPane;
        tabs.ItemsSource = new[] { historyNav, spendingNav, settingsNav }; tabs.SelectedIndex = 0;
        Grid.SetRow(tabs, 1); root.Children.Add(tabs); Content = root;
        BuildSettings(); ApplyTranslations();
        Localization.Changed += ApplyTranslations;
        Closed += (_, _) =>
        {
            Localization.Changed -= ApplyTranslations; modelsVersion++; modelLoad?.Cancel(); modelLoad?.Dispose();
            keySave?.Cancel(); vocabularySave?.Cancel(); urlSave?.Cancel();
            controller.History.Changed -= RefreshHistory; controller.SettingsChanged -= LoadSettings; controller.Usage.Changed -= RefreshUsage;
        };
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            if (tabs.SelectedIndex == 2) { RefreshApiKeyStatus(); _ = RefreshModelsAsync(); }
            if (tabs.SelectedIndex == 1) RefreshSpending();
        };
        cancel.Click += (_, _) => controller.Cancel();
        record.Click += async (_, _) => { if (controller.Recording) await controller.StopRecordingAsync(); else controller.StartRecording(); };
        search.TextChanged += (_, _) => RefreshHistory(); controller.History.Changed += RefreshHistory; controller.SettingsChanged += LoadSettings;
        controller.Usage.Changed += RefreshUsage;
        dictation.ShortcutCommitted += () => Persist(s => s.Hotkey = dictation.Text);
        history.ShortcutCommitted += () => Persist(s => s.HistoryHotkey = history.Text);
        microphone.SelectionChanged += (_, _) => { if (microphone.SelectedItem is Microphone value) Persist(s => s.MicrophoneDevice = value.Id); };
        edge.SelectionChanged += (_, _) => Persist(s => s.IndicatorEdge = edge.SelectedIndex == 1 ? "Left" : "Right");
        restore.IsCheckedChanged += (_, _) => Persist(s => s.RestoreClipboard = restore.IsChecked == true);
        startup.IsCheckedChanged += (_, _) => Persist(s => s.StartWithSystem = startup.IsChecked == true);
        language.SelectionChanged += (_, _) => { if (language.SelectedItem is LanguageOption value) Persist(s => s.Language = value.Code); };
        provider.SelectionChanged += async (_, _) =>
        {
            if (loading || provider.SelectedItem is not ProviderOption value) return;
            SaveApiKeyInput();
            apiKey.Text = "";
            Persist(s => s.Provider = value.Code); RefreshApiKeyStatus();
            await RefreshModelsAsync();
        };
        model.SelectionChanged += (_, _) =>
        {
            if (model.SelectedItem is AudioModel value) Persist(s => { if (s.Provider == Providers.LiteLlm) s.LiteLlmModel = value.Id; else s.Model = value.Id; });
        };
        serverUrl.TextChanged += (_, _) =>
        {
            if (loading || serverUrl.Text?.Trim() == controller.Settings.LiteLlmBaseUrl) return;
            QueueSave(ref urlSave, SaveServerUrl);
        };
        serverUrl.LostFocus += (_, _) => { urlSave?.Cancel(); SaveServerUrl(); };
        vocabulary.TextChanged += (_, _) =>
        {
            if (!loading) QueueSave(ref vocabularySave, () => Persist(s => s.Vocabulary = vocabulary.Text?.Trim() ?? ""));
        };
        vocabulary.LostFocus += (_, _) => { vocabularySave?.Cancel(); if (!loading) Persist(s => s.Vocabulary = vocabulary.Text?.Trim() ?? ""); };
        apiKey.TextChanged += (_, _) => { if (!loading && !string.IsNullOrWhiteSpace(apiKey.Text)) QueueSave(ref keySave, SaveApiKeyInput); };
        apiKey.LostFocus += (_, _) => { keySave?.Cancel(); SaveApiKeyInput(); };
        deleteApiKey.Click += async (_, _) =>
        {
            try { controller.DeleteApiKey(); apiKey.Text = ""; validation.Text = ""; RefreshApiKeyStatus(); await RefreshModelsAsync(); }
            catch (Exception ex) { validation.Text = ex.Message; }
        };
        RefreshApiKeyStatus();
    }
    private void BuildSettings()
    {
        var grid = new Grid { Name = "SettingsFields", ColumnDefinitions = new ColumnDefinitions("155,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        void Row(int row, string title, Control control)
        {
            var label = new TextBlock { Text = Localization.T(title), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap }; labels[title] = label; Grid.SetRow(label, row); grid.Children.Add(label);
            control.VerticalAlignment = VerticalAlignment.Center; control.Margin = new Thickness(0, 2); Grid.SetRow(control, row); Grid.SetColumn(control, 1); grid.Children.Add(control);
        }
        Row(0, "Record", dictation); Row(1, "History", history); Row(2, "Microphone", microphone); Row(3, "Provider", provider); Row(5, "ServerUrl", serverUrl); Row(6, "Model", model); Row(7, "Vocabulary", vocabulary); Row(8, "Indicator", edge); Row(9, "RestoreClipboard", restore); Row(10, "StartWithSystem", startup); Row(11, "Language", language);
        var keyPane = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        keyPane.Children.Add(apiKey); deleteApiKey.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(deleteApiKey, 1); keyPane.Children.Add(deleteApiKey);
        Row(4, "ApiKey", keyPane);
        Grid.SetRow(validation, 12); Grid.SetColumnSpan(validation, 2); grid.Children.Add(validation);
        settingsPane.Content = grid;
        microphone.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Microphone>((value, _) => new TextBlock { Text = value?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        language.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<LanguageOption>((value, _) => new TextBlock { Text = value?.Name });
    }
    private void ApplyTranslations()
    {
        loading = true;
        try
        {
            model.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            model.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AudioModel>((value, _) => ModelSpeedBadge.Create(value));
            AutomationProperties.SetName(search, Localization.T("Search"));
            StandardIcon.SetButton(cancel, "x", Localization.T("Cancel"));
            StandardIcon.SetButton(record, controller.Recording ? "square" : "mic", Localization.T(controller.Recording ? "Stop" : "Record"));
            historyNav.Header = Localization.T("History"); spendingNav.Header = Localization.T("Spending"); settingsNav.Header = Localization.T("Settings");
            StandardIcon.SetButton(deleteApiKey, "trash-2", Localization.T("DeleteApiKey"));
            foreach (var pair in labels) pair.Value.Text = Localization.T(pair.Key);
            edge.ItemsSource = new[] { Localization.T("Right"), Localization.T("Left") };
            language.ItemsSource = new[] { new LanguageOption("system", Localization.T("SystemLanguage")), new LanguageOption("en", Localization.T("LanguageEnglish")), new LanguageOption("ru", Localization.T("LanguageRussian")) };
            provider.ItemsSource = new[] { new ProviderOption(Providers.Gemini, Localization.T("GeminiDirect")), new ProviderOption(Providers.LiteLlm, Localization.T("LiteLlmApiKey")) };
            validation.Text = Localization.TranslateKnown(validation.Text ?? "");
            status.Text = Localization.TranslateKnown(status.Text ?? "");
        }
        finally { loading = false; }
        LoadSettings(); RefreshHistory(); RefreshSpending(); RefreshApiKeyStatus();
    }
    private void RefreshApiKeyStatus()
    {
        string selected = controller.Settings.Provider;
        _ = Configuration.GetApiKey(selected);
        labels["ApiKey"].Text = Localization.T(selected == Providers.LiteLlm ? "LiteLlmApiKey" : "ApiKey");
        if (Configuration.ApiKeyStorageError != null) validation.Text = Configuration.ApiKeyStorageError;
        deleteApiKey.IsEnabled = Configuration.HasSavedKey(selected);
    }
    private void LoadSettings()
    {
        loading = true;
        try
        {
            var s = controller.Settings; dictation.Text = s.Hotkey; history.Text = s.HistoryHotkey;
            if (!vocabulary.IsFocused) vocabulary.Text = s.Vocabulary;
            provider.SelectedItem = ((IEnumerable<ProviderOption>)provider.ItemsSource!).First(x => x.Code == s.Provider);
            bool lite = s.Provider == Providers.LiteLlm;
            serverUrl.IsVisible = labels["ServerUrl"].IsVisible = lite;
            if (!serverUrl.IsFocused) serverUrl.Text = s.LiteLlmBaseUrl;
            if (modelsProvider != s.Provider) { audioModels = lite ? [] : TranscriptionClient.GeminiModels; modelsProvider = s.Provider; }
            model.ItemsSource = audioModels; model.SelectedItem = audioModels.FirstOrDefault(x => x.Id == s.ActiveModel);
            restore.IsChecked = s.RestoreClipboard; startup.IsChecked = s.StartWithSystem; edge.SelectedIndex = s.IndicatorEdge == "Left" ? 1 : 0;
            language.SelectedItem = ((IEnumerable<LanguageOption>)language.ItemsSource!).First(x => x.Code == s.Language);
            var devices = controller.Recorder.Devices(); microphone.ItemsSource = devices; microphone.SelectedItem = devices.FirstOrDefault(x => x.Id == s.MicrophoneDevice) ?? devices[0];
        }
        finally { loading = false; }
    }
    internal async Task RefreshModelsAsync()
    {
        int version = ++modelsVersion;
        modelLoad?.Cancel(); modelLoad?.Dispose(); modelLoad = new();
        var settings = controller.Settings.Clone();
        model.IsEnabled = false; ModelLoadError = null;
        try
        {
            var found = await controller.Transcriber.GetModelsAsync(settings, Configuration.GetApiKey(settings.Provider), modelLoad.Token, refresh: true);
            if (version != modelsVersion) return;
            audioModels = found; modelsProvider = settings.Provider;
            LoadSettings(); ModelLoadError = controller.Transcriber.CatalogWarning; validation.Text = ModelLoadError ?? "";
            if (!found.Any(x => x.Id == controller.Settings.ActiveModel))
            {
                var first = found.FirstOrDefault(x => x.Id == "gemini-2.5-flash") ?? found[0];
                Persist(s => { if (s.Provider == Providers.LiteLlm) s.LiteLlmModel = first.Id; else s.Model = first.Id; });
            }
        }
        catch (Exception ex)
        {
            if (version != modelsVersion) return;
            // A LiteLLM list is valid only for the connection that returned it.
            if (settings.Provider == Providers.LiteLlm) { audioModels = []; modelsProvider = settings.Provider; LoadSettings(); }
            ModelLoadError = ex is InvalidOperationException or FormatException ? ex.Message : Localization.T("ModelsLoadFailed");
            validation.Text = ModelLoadError;
        }
        finally { if (version == modelsVersion) model.IsEnabled = audioModels.Count > 0; }
    }
    private static void QueueSave(ref CancellationTokenSource? pending, Action save)
    {
        pending?.Cancel(); pending?.Dispose(); pending = new();
        _ = SaveAfterPauseAsync(pending.Token, save);
    }
    private static async Task SaveAfterPauseAsync(CancellationToken cancellation, Action save)
    {
        try { await Task.Delay(500, cancellation); save(); }
        catch (OperationCanceledException) { }
    }
    private void SaveServerUrl()
    {
        if (loading || serverUrl.Text?.Trim() == controller.Settings.LiteLlmBaseUrl) return;
        string value = serverUrl.Text?.Trim() ?? "";
        try { if (value.Length > 0) TranscriptionClient.BaseUri(value); }
        catch (FormatException ex) { validation.Text = ex.Message; return; }
        Persist(s => s.LiteLlmBaseUrl = value);
        _ = RefreshModelsAsync();
    }
    private void SaveApiKeyInput()
    {
        if (loading || string.IsNullOrWhiteSpace(apiKey.Text)) return;
        try { controller.SaveApiKey(apiKey.Text); apiKey.Text = ""; validation.Text = ""; RefreshApiKeyStatus(); _ = RefreshModelsAsync(); }
        catch (Exception ex) { validation.Text = ex.Message; }
    }
    private void Persist(Action<Settings> change)
    {
        if (loading) return;
        var next = controller.Settings.Clone(); change(next);
        try { controller.CommitSettings(next); validation.Text = ""; }
        catch (Exception ex) { validation.Text = ex.Message; LoadSettings(); }
    }
    public void OpenHistory() { tabs.SelectedIndex = 0; Show(); WindowState = WindowState.Normal; Activate(); RefreshHistory(); }
    public void OpenSettings() { tabs.SelectedIndex = 2; Show(); WindowState = WindowState.Normal; Activate(); }
    internal void OpenSpending() { tabs.SelectedIndex = 1; RefreshSpending(); }
    public void UpdateStatus(string? text, bool busy, bool recording, bool error = false)
    {
        if (text != null) { status.Text = text; ToolTip.SetTip(status, text); status.Foreground = Brush.Parse(error ? "#BB526D" : "#9393A6"); }
        cancel.IsVisible = busy;
        StandardIcon.SetButton(record, recording ? "square" : "mic", Localization.T(recording ? "Stop" : "Record"));
    }
    private static ScrollViewer PageScroll(string name) => new()
    {
        Name = name, AllowAutoHide = false,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Padding = new Thickness(0, 0, 8, 0)
    };
    private static Border CostBadge(string caption, string tooltip)
    {
        var badge = new Border
        {
            Name = "RequestCostBadge", Background = Brush.Parse("#F2F0FC"), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3), Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock { Text = caption, FontSize = 11, Foreground = Brush.Parse("#7160DF") }
        };
        ToolTip.SetTip(badge, tooltip); return badge;
    }
    private void BuildSpending()
    {
        spendingPane.Children.Add(spendingTotals);
        var scroll = PageScroll("SpendingScroll"); scroll.Content = dailySpending;
        Grid.SetRow(scroll, 1); spendingPane.Children.Add(scroll);
    }
    private void RefreshUsage() { RefreshHistory(); RefreshSpending(); }
    private static string TotalCost(IEnumerable<RequestCharge> entries)
    {
        var all = entries.ToArray();
        if (all.Length > 0 && all.All(x => x.CostUsd == null)) return Localization.T("CostUnknown");
        string amount = Money.Format(all.Sum(x => x.CostUsd ?? 0), all.Any(x => x.Estimated));
        return all.Any(x => x.CostUsd == null) ? Localization.T("KnownCost", amount) : amount;
    }
    private void RefreshSpending()
    {
        spendingTotals.Children.Clear(); dailySpending.Children.Clear();
        var today = DateTime.Today;
        var all = controller.Usage.Entries;
        var groups = new[]
        {
            ("Today", all.Where(x => x.CreatedAt.LocalDateTime.Date == today)),
            ("ThisMonth", all.Where(x => x.CreatedAt.LocalDateTime.Year == today.Year && x.CreatedAt.LocalDateTime.Month == today.Month)),
            ("AllTime", all.AsEnumerable())
        };
        for (int i = 0; i < groups.Length; i++)
        {
            var values = groups[i].Item2.ToArray();
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock { Text = Localization.T(groups[i].Item1), FontSize = 12, Foreground = Brush.Parse("#807C93") });
            content.Children.Add(new TextBlock { Text = TotalCost(values), FontSize = 19, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock { Text = Localization.T("RequestCount", values.Length), FontSize = 11, Foreground = Brush.Parse("#9393A6") });
            var box = new Border { Child = content, Background = Brush.Parse("#F7F7FB"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
            ToolTip.SetTip(box, TotalCost(values)); Grid.SetColumn(box, i); spendingTotals.Children.Add(box);
        }
        dailySpending.Children.Add(new TextBlock { Text = Localization.T("DailySpending"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var days = controller.Usage.Days();
        if (days.Count == 0) dailySpending.Children.Add(new TextBlock { Text = Localization.T("NoSpending"), TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9393A6") });
        foreach (var day in days)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            row.Children.Add(new TextBlock { Text = day.Day.ToString("dd MMM yyyy", Localization.Culture), FontWeight = FontWeight.Medium });
            string amount = day.Requests == day.Unknown ? Localization.T("CostUnknown") : Money.Format(day.CostUsd, day.Estimated);
            if (day.Unknown > 0 && day.Requests != day.Unknown) amount = Localization.T("KnownCost", amount);
            var cost = new TextBlock { Text = amount, FontWeight = FontWeight.SemiBold }; Grid.SetColumn(cost, 1); row.Children.Add(cost);
            var count = new TextBlock { Text = Localization.T("RequestCount", day.Requests) + (day.Unknown > 0 ? " · " + Localization.T("UnpricedCount", day.Unknown) : ""), Foreground = Brush.Parse("#9393A6"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(count, 1); Grid.SetColumnSpan(count, 2); row.Children.Add(count);
            dailySpending.Children.Add(new Border { Child = row, Padding = new Thickness(12, 10), BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) });
        }
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
            var metadata = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            metadata.Children.Add(new TextBlock { Text = entry.TimeLabel, FontSize = 11, Foreground = Brush.Parse("#9393A6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            top.Children.Add(metadata);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            var copy = new Button { Classes = { "small" }, IsEnabled = entry.Text.Length > 0 };
            StandardIcon.SetButton(copy, "copy", Localization.T("Copy"), compact: true);
            copy.Click += async (_, _) => { try { await controller.Inserter.CopyAsync(entry.Text); } catch (Exception ex) { controller.SetStatus(ex.Message); } }; buttons.Children.Add(copy);
            if (entry.CanRetry) { var retry = new Button { Classes = { "small" }, IsEnabled = !controller.IsProcessing(entry.Id) }; StandardIcon.SetButton(retry, "rotate-cw", Localization.T("Retry"), compact: true); retry.Click += (_, _) => controller.Retry(entry); buttons.Children.Add(retry); }
            var delete = new Button { Classes = { "small" }, IsEnabled = !controller.IsProcessing(entry.Id) };
            StandardIcon.SetButton(delete, "trash-2", Localization.T("Delete"), compact: true);
            delete.Click += (_, _) => { if (!controller.IsProcessing(entry.Id)) { try { controller.History.Delete(entry); } catch (Exception ex) { controller.SetStatus(ex.Message); } } }; buttons.Children.Add(delete);
            Grid.SetColumn(buttons, 1); top.Children.Add(buttons); card.Children.Add(top);
            var charges = controller.Usage.Entries.Where(x => x.HistoryId == entry.Id).OrderBy(x => x.CreatedAt).ToArray();
            if (charges.Length == 0) metadata.Children.Add(CostBadge(Localization.T("CostUnknown"), Localization.T("CostLegacy")));
            for (int i = 0; i < charges.Length; i++)
            {
                var charge = charges[i];
                string caption = charges.Length > 1 ? Localization.T("RequestCost", i + 1, charge.CostLabel) : charge.CostLabel;
                metadata.Children.Add(CostBadge(caption, charge.Model + " · " + Localization.T(charge.CostUsd == null ? "CostUnavailable" : charge.Estimated ? "CostEstimated" : "CostReported")));
            }
            card.Children.Add(new SelectableTextBlock { Text = entry.DisplayText, TextWrapping = TextWrapping.Wrap });
            records.Children.Add(new Border { Child = card, BorderBrush = Brush.Parse("#E9E9F1"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 9) });
        }
    }
}
