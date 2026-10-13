using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Govori;

internal sealed class OverlayWindow : Window
{
    internal const int PhysicalSize = 18;
    private readonly IDesktopPlatform platform;
    private readonly RecordingDot dot = new();
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 13 };
    private readonly TextBlock errorTitle = new() { Foreground = Brush.Parse("#FF8A8A"), FontSize = 14, FontWeight = FontWeight.SemiBold };
    private readonly Window errorNotice;
    private readonly DispatcherTimer errorTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    internal string State => dot.State;
    internal bool ErrorNoticeVisible => errorNotice.IsVisible;
    internal Window ErrorNotice => errorNotice;
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
        var message = new StackPanel { Spacing = 6 };
        message.Children.Add(errorTitle); message.Children.Add(errorText);
        errorNotice = new Window
        {
            Width = 320, MinWidth = 1, MinHeight = 1, SizeToContent = SizeToContent.Height,
            CanResize = false, SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, IsHitTestVisible = false,
            Background = Brush.Parse("#2C2840"),
            Content = new Border { Padding = new Thickness(16), Child = message }
        };
        errorNotice.Opened += (_, _) => platform.ConfigureOverlay(errorNotice);
        errorTimer.Tick += (_, _) => Hide();
        Closed += (_, _) => { errorTimer.Stop(); errorNotice.Close(); };
    }
    public void ShowState(nint target, string edge, string state)
    {
        errorTimer.Stop(); errorNotice.Hide();
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
    public void ShowError(nint target, string edge, string message)
    {
        ShowState(target, edge, "error");
        errorTitle.Text = Localization.T("RecordingFailed"); errorText.Text = message;
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1;
        var area = screen?.WorkingArea ?? new PixelRect(Position.X, Position.Y, 800, 600);
        errorNotice.Width = Math.Min(320, (area.Width - 36) / scaling);
        errorNotice.Show();
        int width = (int)Math.Round(errorNotice.Bounds.Width * scaling);
        int height = (int)Math.Round(errorNotice.Bounds.Height * scaling);
        errorNotice.Position = new PixelPoint(edge == "Left" ? area.X + PhysicalSize + 18 : area.Right - width - PhysicalSize - 18,
            Math.Clamp(Position.Y - height / 2, area.Y, Math.Max(area.Y, area.Bottom - height)));
        platform.ConfigureOverlay(errorNotice);
        errorTimer.Start();
    }
    public new void Hide() { errorTimer.Stop(); errorNotice.Hide(); base.Hide(); }
    public void UpdateLevel(float level) { dot.Level = level; dot.InvalidateVisual(); }

    private sealed class RecordingDot : Control
    {
        public string State { get; set; } = "starting";
        public float Level { get; set; }
        private static readonly IBrush Backdrop = Brush.Parse("#F52C2840");
        private static readonly IBrush Starting = Brush.Parse("#9CA3AF");
        private static readonly IBrush Recording = Brush.Parse("#34EB8F");
        private static readonly IBrush Processing = Brush.Parse("#F2AD56");
        private static readonly IBrush Error = Brush.Parse("#FF6262");
        private static readonly IBrush Ring = Brush.Parse("#C1FADF");
        public override void Render(DrawingContext context)
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double outerRadius = Math.Min(Bounds.Width, Bounds.Height) / 2;
            context.DrawEllipse(Backdrop, null, center, outerRadius, outerRadius);
            double radius = Math.Max(0, outerRadius - 2 / scaling);
            if (State == "recording") radius *= 0.86 + Math.Min(1, Level * 5) * 0.14;
            context.DrawEllipse(State == "recording" ? Recording : State == "processing" ? Processing : State == "error" ? Error : Starting, null, center, radius, radius);
            if (State == "error")
            {
                var pen = new Pen(Brushes.White, 1.5 / scaling);
                double offset = radius * 0.4;
                context.DrawLine(pen, new Point(center.X - offset, center.Y - offset), new Point(center.X + offset, center.Y + offset));
                context.DrawLine(pen, new Point(center.X + offset, center.Y - offset), new Point(center.X - offset, center.Y + offset));
            }
            if (State == "recording")
            {
                double ringRadius = Math.Max(0, outerRadius - 1 / scaling);
                context.DrawEllipse(null, new Pen(Ring, 1 / scaling), center, ringRadius, ringRadius);
            }
        }
    }
}
