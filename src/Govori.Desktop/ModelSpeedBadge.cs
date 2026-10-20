using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Govori;

internal static class ModelSpeedBadge
{
    // Direct Gemini API, 2026-10-19: mean of three runs on the 12.944-second recording.
    // All runs on this recording returned text. Other samples included empty responses.
    // Sources: artifacts/benchmark-flash/report.md and artifacts/benchmark-remaining/report.md.
    // No estimates for unmeasured model IDs.
    private static readonly IReadOnlyDictionary<string, double> Seconds = new Dictionary<string, double>
    {
        ["gemini-3.1-flash-lite"] = 1.73,
        ["gemini-2.5-flash"] = 1.78,
        ["gemini-2.5-flash-lite"] = 1.79,
        ["gemini-3.5-flash-lite"] = 2.24,
        ["gemini-3.1-flash-lite-preview"] = 2.35,
        ["gemini-3.6-flash"] = 3.19,
        ["gemini-3.5-flash"] = 3.24,
        ["gemini-3-flash-preview"] = 3.52,
        ["gemini-3.8-flash"] = 4.04,
        ["gemini-3.7-flash"] = 5.63,
        ["gemini-2.5-pro"] = 6.18,
        ["gemini-3.1-pro-preview"] = 14.61,
        ["gemini-3.1-pro-preview-customtools"] = 23.05
    };

    internal static Control Create(AudioModel? model)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        if (model == null) return row;
        row.Children.Add(new TextBlock { Text = model.Id, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(row, model.Id);
        if (!Seconds.TryGetValue(model.Id, out double seconds)) return row;
        // Fast <2.5s, medium <4s, slow >=4s on the same audio sample.
        var (key, color) = seconds < 2.5 ? ("ModelSpeedFast", "#238551")
            : seconds < 4 ? ("ModelSpeedMedium", "#B77900") : ("ModelSpeedSlow", "#CF4655");
        string description = Localization.T("ModelSpeedMeasured", Localization.T(key), seconds);
        var icon = new StandardIcon("timer", 17) { Name = "ModelSpeedIcon", Foreground = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 1); row.Children.Add(icon);
        ToolTip.SetTip(row, description);
        AutomationProperties.SetName(row, model.Id + ": " + description);
        return row;
    }
}
