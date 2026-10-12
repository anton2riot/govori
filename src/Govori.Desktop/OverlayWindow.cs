using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Govori;

internal sealed class OverlayWindow : Window
{
    internal const int PhysicalSize = 18;
    private readonly IDesktopPlatform platform;
    private readonly RecordingDot dot = new();
    internal string State => dot.State;
    public OverlayWindow(IDesktopPlatform platform)
    {
        this.platform = platform; Width = PhysicalSize; Height = PhysicalSize; CanResize = false;
        // Avalonia leaves Win32's default minimum untouched when this is zero.
        MinWidth = MinHeight = 1; Padding = new Thickness(0); BorderThickness = new Thickness(0);
        SystemDecorations = SystemDecorations.None; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = dot; IsHitTestVisible = false;
        Template = new Avalonia.Controls.Templates.FuncControlTemplate<Window>((_, _) =>
            new Avalonia.Controls.Presenters.ContentPresenter { Name = "PART_ContentPresenter", Content = dot });
        Opened += (_, _) => platform.ConfigureOverlay(this);
    }
    public void ShowState(nint target, string edge, string state)
    {
        var screen = Screens.Primary;
        if (OperatingSystem.IsWindows() && target != 0 && WindowsPlatform.GetWindowRect(target, out var rect))
            screen = Screens.ScreenFromPoint(new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) ?? screen;
        if (screen != null)
        {
            double size = PhysicalSize / screen.Scaling;
            MinWidth = MinHeight = size;
            Width = Height = size;
            var area = screen.WorkingArea;
            Position = new PixelPoint(edge == "Left" ? area.X + 6 : area.Right - PhysicalSize - 6, area.Y + (area.Height - PhysicalSize) / 2);
        }
        dot.State = state; dot.Level = 0; dot.InvalidateVisual();
        Show(); platform.ConfigureOverlay(this);
    }
    public void UpdateLevel(float level) { dot.Level = level; dot.InvalidateVisual(); }

    private sealed class RecordingDot : Control
    {
        public string State { get; set; } = "starting";
        public float Level { get; set; }
        private static readonly IBrush Backdrop = Brush.Parse("#F52C2840");
        private static readonly IBrush Starting = Brush.Parse("#9CA3AF");
        private static readonly IBrush Recording = Brush.Parse("#34EB8F");
        private static readonly IBrush Processing = Brush.Parse("#F2AD56");
        private static readonly IBrush Ring = Brush.Parse("#C1FADF");
        public override void Render(DrawingContext context)
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double outerRadius = Math.Min(Bounds.Width, Bounds.Height) / 2;
            context.DrawEllipse(Backdrop, null, center, outerRadius, outerRadius);
            double radius = Math.Max(0, outerRadius - 2 / scaling);
            if (State == "recording") radius *= 0.86 + Math.Min(1, Level * 5) * 0.14;
            context.DrawEllipse(State == "recording" ? Recording : State == "processing" ? Processing : Starting, null, center, radius, radius);
            if (State == "recording")
            {
                double ringRadius = Math.Max(0, outerRadius - 1 / scaling);
                context.DrawEllipse(null, new Pen(Ring, 1 / scaling), center, ringRadius, ringRadius);
            }
        }
    }
}
