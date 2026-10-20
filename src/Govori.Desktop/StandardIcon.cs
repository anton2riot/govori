using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;

namespace Govori;

// Lucide paths, rendered locally with the original 24×24 view box and 2px stroke.
// Copyright and license: ThirdPartyNotices/Lucide-LICENSE.txt.
internal sealed class StandardIcon : TemplatedControl
{
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["mic"] = "M12 19v3 M19 10v2a7 7 0 0 1-14 0v-2 M12 2a3 3 0 0 1 3 3v7a3 3 0 0 1-6 0V5a3 3 0 0 1 3-3Z",
        ["history"] = "M3 12a9 9 0 1 0 9-9a9.75 9.75 0 0 0-6.74 2.74L3 8 M3 3v5h5m4-1v5l4 2",
        ["settings"] = "M9.671 4.136a2.34 2.34 0 0 1 4.659 0a2.34 2.34 0 0 0 3.319 1.915a2.34 2.34 0 0 1 2.33 4.033a2.34 2.34 0 0 0 0 3.831a2.34 2.34 0 0 1-2.33 4.033a2.34 2.34 0 0 0-3.319 1.915a2.34 2.34 0 0 1-4.659 0a2.34 2.34 0 0 0-3.32-1.915a2.34 2.34 0 0 1-2.33-4.033a2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915 M15 12a3 3 0 1 0-6 0a3 3 0 1 0 6 0",
        ["copy"] = "M10 8H20a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2Z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2",
        ["rotate-cw"] = "M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8 M21 3v5h-5",
        ["trash-2"] = "M10 11v6m4-6v6m5-11v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2",
        ["x"] = "M18 6L6 18M6 6l12 12",
        ["square"] = "M5 3H19a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z",
        ["search"] = "M21 21l-4.34-4.34 M19 11a8 8 0 1 0-16 0a8 8 0 1 0 16 0",
        ["timer"] = "M10 2h4m-2 12l3-3 M20 14a8 8 0 1 0-16 0a8 8 0 1 0 16 0"
    }.ToDictionary(x => x.Key, x => Geometry.Parse(x.Value));

    internal string Kind { get; }
    static StandardIcon() => AffectsRender<StandardIcon>(ForegroundProperty);
    internal StandardIcon(string kind, double size = 18)
    {
        Kind = kind; Width = Height = size; IsHitTestVisible = false;
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var transform = context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24));
        context.DrawGeometry(null, new Pen(Foreground, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Shapes[Kind]);
    }
    internal static void SetButton(Button button, string kind, string caption, bool compact = false)
    {
        if (button.Content is not StandardIcon icon || icon.Kind != kind)
        {
            icon = new StandardIcon(kind, compact ? 16 : 18);
            icon.Bind(ForegroundProperty, new Binding(nameof(Foreground)) { Source = button });
            button.Content = icon;
        }
        button.Width = button.Height = compact ? 30 : 36;
        button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        button.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        if (!button.Classes.Contains("icon")) button.Classes.Add("icon");
        AutomationProperties.SetName(button, caption);
        ToolTip.SetTip(button, caption);
    }
}
