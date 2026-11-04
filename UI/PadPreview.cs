using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace TouchPadAdvanced;

internal enum PadRegion { Top, Bottom, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight }

internal sealed class PadPreview : Grid
{
    private const double Inset = 12;
    private readonly Canvas canvas = new();
    private readonly TextBlock rangeLabel = new() { FontSize = 12, Opacity = 0.8, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Rectangle board = new() { RadiusX = 16, RadiusY = 16, IsHitTestVisible = false, StrokeThickness = 1.5 };
    private readonly Rectangle[] bands = Enumerable.Range(0, 8)
        .Select(_ => new Rectangle { IsHitTestVisible = false, RadiusX = 6, RadiusY = 6 }).ToArray();
    private readonly Button[] regions = new Button[8];
    private readonly Thumb[] handles = new Thumb[4];
    private readonly Border[] handleBadges = Enumerable.Range(0, 4).Select(_ => new Border
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 2, 6, 2),
        Background = Brush(220, 32, 84, 195),
        BorderBrush = Brush(255, 130, 175, 255),
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        Child = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
    }).ToArray();
    private readonly TextBlock centerTitle = new() { Text = "触控板", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock centerSubtitle = new() { FontSize = 12, Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Button center;
    private readonly Line trajectoryLine = new()
    {
        StrokeThickness = 2.5,
        StrokeDashArray = [3, 2],
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly Ellipse originRing = new()
    {
        Width = 18, Height = 18, StrokeThickness = 2,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly Ellipse[] dots = Enumerable.Range(0, 5).Select(_ => new Ellipse
        { Width = 26, Height = 26, StrokeThickness = 2, IsHitTestVisible = false }).ToArray();
    private readonly TextBlock[] ids = Enumerable.Range(0, 5).Select(_ => new TextBlock
        { Width = 26, Height = 26, TextAlignment = TextAlignment.Center,
          Padding = new Thickness(0, 3, 0, 0), IsHitTestVisible = false }).ToArray();
    private readonly InputSystemCursor verticalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    private readonly InputSystemCursor horizontalCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    private readonly InputSystemCursor arrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly Dictionary<GestureId, GestureAction> draftActions = [];
    private TouchFrame? lastFrame;
    private GestureSettings? lastSettings;
    private (double X, double Y)? lastOrigin;
    private GesturePhase lastPhase;
    private PadRegion? highlightedRegion;
    private DateTimeOffset highlightUntil;
    private double dragOrigin, dragDisplacement;
    private bool resizing;
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    internal EdgeRecognitionRanges Ranges { get; private set; } = new();
    internal PadRegion? SelectedRegion { get; private set; }
    internal double AspectRatio { get; set; } = 1.5;
    internal event Action? RangesChanged;
    internal event Action<PadRegion?>? RegionSelected;
    internal int HandleCount => handles.Count(h => h.Parent == canvas);
    internal int RegionCount => regions.Count(b => b.Parent == canvas);
    internal bool IsResizing { get => resizing; set { resizing = value; Render(); } }

    internal PadPreview()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(canvas);
        Grid.SetRow(rangeLabel, 1); Children.Add(rangeLabel);
        board.Fill = Brush(32, 110, 120, 145);
        board.Stroke = Brush(120, 110, 120, 145);
        canvas.Children.Add(board);
        foreach (var shape in bands) canvas.Children.Add(shape);
        center = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { centerTitle, centerSubtitle }
            }
        };
        canvas.Children.Add(center);
        center.Click += (_, _) => SelectRegion(null);
        foreach (var region in Enum.GetValues<PadRegion>())
        {
            string name = RegionName(region);
            var button = new Button
            {
                Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(8),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, name + "手势设置");
            ToolTipService.SetToolTip(button, name + "：点击设置功能");
            button.Click += (_, _) => SelectRegion(region);
            regions[(int)region] = button;
            canvas.Children.Add(button);
        }
        foreach (var edge in Enum.GetValues<PadEdge>())
        {
            var handle = new Thumb { Style = (Style)Application.Current.Resources["EdgeResizeThumbStyle"] };
            handle.PointerEntered += (_, _) => ProtectedCursor = edge is PadEdge.Top or PadEdge.Bottom
                ? verticalCursor : horizontalCursor;
            handle.PointerExited += (_, _) => ProtectedCursor = arrowCursor;
            string name = RegionName((PadRegion)edge);
            AutomationProperties.SetName(handle, name + "识别范围");
            AutomationProperties.SetHelpText(handle, "拖动调整，或使用方向键微调。范围为 2% 至 20%。");
            ToolTipService.SetToolTip(handle, name + "：拖动调整，方向键微调");
            handle.DragStarted += (_, _) => { dragOrigin = Ranges.Width(edge); dragDisplacement = 0; };
            handle.DragDelta += (_, e) =>
            {
                double extent = edge is PadEdge.Top or PadEdge.Bottom ? BoardHeight : BoardWidth;
                double delta = edge is PadEdge.Top or PadEdge.Bottom ? e.VerticalChange : e.HorizontalChange;
                dragDisplacement += delta / extent * (edge is PadEdge.Bottom or PadEdge.Right ? -1 : 1);
                ChangeWidth(edge, dragOrigin + dragDisplacement);
            };
            handle.KeyDown += (_, e) =>
            {
                int direction = (edge, e.Key) switch
                {
                    (PadEdge.Top, VirtualKey.Down) or (PadEdge.Bottom, VirtualKey.Up) or
                    (PadEdge.Left, VirtualKey.Right) or (PadEdge.Right, VirtualKey.Left) => 1,
                    (PadEdge.Top, VirtualKey.Up) or (PadEdge.Bottom, VirtualKey.Down) or
                    (PadEdge.Left, VirtualKey.Left) or (PadEdge.Right, VirtualKey.Right) => -1,
                    _ => 0
                };
                if (direction == 0) return;
                ChangeWidth(edge, Ranges.Width(edge) + direction * .005);
                e.Handled = true;
            };
            handles[(int)edge] = handle;
            canvas.Children.Add(handle);
            canvas.Children.Add(handleBadges[(int)edge]);
        }
        canvas.Children.Add(trajectoryLine);
        canvas.Children.Add(originRing);
        for (int i = 0; i < dots.Length; i++) { canvas.Children.Add(dots[i]); canvas.Children.Add(ids[i]); }
        canvas.SizeChanged += (_, _) => Render();
    }

    internal static string RegionName(PadRegion region) => region switch
    {
        PadRegion.Top => "上边缘", PadRegion.Bottom => "下边缘",
        PadRegion.Left => "左边缘", PadRegion.Right => "右边缘",
        PadRegion.TopLeft => "左上角", PadRegion.TopRight => "右上角",
        PadRegion.BottomLeft => "左下角", _ => "右下角"
    };

    private static PadRegion GestureRegion(GestureId id)
    {
        var spec = GestureCatalog.Get(id);
        if (spec.Corner is { } c) return c switch
        {
            PadCorner.TopLeft => PadRegion.TopLeft, PadCorner.TopRight => PadRegion.TopRight,
            PadCorner.BottomLeft => PadRegion.BottomLeft, _ => PadRegion.BottomRight
        };
        return (PadRegion)spec.Edge!.Value;
    }

    private static string ShortAction(GestureAction action) => action switch
    {
        GestureAction.None => "",
        GestureAction.Back => "后退",
        GestureAction.Forward => "前进",
        GestureAction.VolumeUp => "音量+",
        GestureAction.VolumeDown => "音量-",
        GestureAction.Mute => "静音",
        GestureAction.PlayPause => "播/暂",
        GestureAction.PreviousTrack => "上一曲",
        GestureAction.NextTrack => "下一曲",
        GestureAction.Shortcut => "快捷键",
        GestureAction.SwitchApp or GestureAction.AppSwitcher => "下个任务",
        GestureAction.PreviousTask => "上个任务",
        GestureAction.TaskView => "任务视图",
        GestureAction.StartMenu => "开始菜单",
        GestureAction.ShowDesktop => "显示桌面",
        GestureAction.PreviousDesktop => "左桌面",
        GestureAction.NextDesktop => "右桌面",
        GestureAction.QuickSettings => "快速设置",
        GestureAction.Notifications => "通知中心",
        GestureAction.FileExplorer => "资源管理器",
        GestureAction.ScrollUp => "上滚轮",
        GestureAction.ScrollDown => "下滚轮",
        GestureAction.ScrollLeft => "左滚轮",
        GestureAction.ScrollRight => "右滚轮",
        GestureAction.BrightnessUp => "亮度+",
        GestureAction.BrightnessDown => "亮度-",
        _ => action.ToString()
    };

    internal void SetDraftBindings(IEnumerable<(GestureId Id, GestureAction Action)> items)
    {
        draftActions.Clear();
        foreach (var (id, action) in items) draftActions[id] = action;
        Render();
    }

    internal void HighlightGesture(GestureId id)
    {
        highlightedRegion = GestureRegion(id);
        highlightUntil = DateTimeOffset.UtcNow.AddMilliseconds(650);
        Render();
    }

    private GestureAction ActionFor(GestureId id) =>
        draftActions.TryGetValue(id, out var action) ? action :
        lastSettings?.Binding(id).Action ?? GestureAction.None;

    private double BoardWidth => Math.Max(1, canvas.ActualWidth - 2 * Inset);
    private double BoardHeight => Math.Max(1, canvas.ActualHeight - 2 * Inset);

    internal void SelectRegion(PadRegion? region)
    {
        if (SelectedRegion == region && region is not null) return;
        SelectedRegion = region;
        Render();
        RegionSelected?.Invoke(region);
    }

    internal void SetRanges(EdgeRecognitionRanges ranges) { Ranges = ranges; Render(); }

    internal void ChangeWidth(PadEdge edge, double width)
    {
        var next = Ranges.WithWidth(edge, width);
        if (next == Ranges) return;
        SetRanges(next);
        RangesChanged?.Invoke();
    }

    internal void Update(TouchFrame? frame, GestureSettings settings,
        (double X, double Y)? activeOrigin = null, GesturePhase phase = GesturePhase.Idle)
    {
        bool highlightExpired = highlightedRegion is not null && DateTimeOffset.UtcNow >= highlightUntil;
        if (highlightExpired) highlightedRegion = null;
        if (frame == lastFrame && settings == lastSettings && activeOrigin == lastOrigin &&
            phase == lastPhase && !highlightExpired) return;
        lastFrame = frame;
        lastSettings = settings;
        lastOrigin = activeOrigin;
        lastPhase = phase;
        Render();
    }

    private void UpdateRegionButtonContent(PadRegion region, bool compact = false)
    {
        var button = regions[(int)region];
        string name = RegionName(region);
        if (region is PadRegion.Top or PadRegion.Bottom)
        {
            var ids = region == PadRegion.Top
                ? new[] { ("←", GestureId.TopSlideLeft), ("→", GestureId.TopSlideRight), ("↓", GestureId.TopInward) }
                : new[] { ("←", GestureId.BottomSlideLeft), ("→", GestureId.BottomSlideRight), ("↑", GestureId.BottomInward) };
            var active = ids.Select(x => (x.Item1, Label: ShortAction(ActionFor(x.Item2))))
                .Where(x => x.Label.Length > 0).Select(x => $"{x.Item1}{x.Label}").ToArray();
            string summary = active.Length > 0 ? string.Join(" · ", active) : "未绑定";
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = summary, FontSize = 11, Opacity = active.Length > 0 ? 0.92 : 0.55, VerticalAlignment = VerticalAlignment.Center }
                }
            };
        }
        else if (region is PadRegion.Left or PadRegion.Right)
        {
            var ids = region == PadRegion.Left
                ? new[] { ("↑", GestureId.LeftSlideUp), ("↓", GestureId.LeftSlideDown), ("→", GestureId.LeftInward) }
                : new[] { ("↑", GestureId.RightSlideUp), ("↓", GestureId.RightSlideDown), ("←", GestureId.RightInward) };
            var active = ids.Select(x => (x.Item1, Label: ShortAction(ActionFor(x.Item2))))
                .Where(x => x.Label.Length > 0).Select(x => $"{x.Item1}{x.Label}").ToArray();
            string verticalTitle = region == PadRegion.Left ? "左边缘" : "右边缘";
            string sub = active.Length > 0 ? string.Join("\n", active) : "未绑定";
            var stack = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(new TextBlock { Text = verticalTitle, FontSize = 11, TextAlignment = TextAlignment.Center });
            if (!compact)
                stack.Children.Add(new TextBlock { Text = sub, FontSize = 10, Opacity = active.Length > 0 ? 0.92 : 0.55, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            button.Content = stack;
        }
        else
        {
            var (arrow, id) = region switch
            {
                PadRegion.TopLeft => ("↘", GestureId.TopLeftInward),
                PadRegion.TopRight => ("↙", GestureId.TopRightInward),
                PadRegion.BottomLeft => ("↗", GestureId.BottomLeftInward),
                _ => ("↖", GestureId.BottomRightInward)
            };
            string shortLabel = ShortAction(ActionFor(id));
            button.Content = shortLabel.Length > 0 && !compact
                ? new StackPanel
                {
                    Spacing = 0,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = arrow, FontSize = 12, TextAlignment = TextAlignment.Center },
                        new TextBlock { Text = shortLabel.Length > 3 ? shortLabel[..3] : shortLabel, FontSize = 9, Opacity = 0.9, TextAlignment = TextAlignment.Center }
                    }
                }
                : new TextBlock { Text = arrow, FontSize = 12, Opacity = shortLabel.Length > 0 ? 0.95 : 0.65, TextAlignment = TextAlignment.Center };
        }
    }

    private int BoundCountForRegion(PadRegion region) => region switch
    {
        PadRegion.Top => new[] { GestureId.TopSlideLeft, GestureId.TopSlideRight, GestureId.TopInward }.Count(id => ActionFor(id) != GestureAction.None),
        PadRegion.Bottom => new[] { GestureId.BottomSlideLeft, GestureId.BottomSlideRight, GestureId.BottomInward }.Count(id => ActionFor(id) != GestureAction.None),
        PadRegion.Left => new[] { GestureId.LeftSlideUp, GestureId.LeftSlideDown, GestureId.LeftInward }.Count(id => ActionFor(id) != GestureAction.None),
        PadRegion.Right => new[] { GestureId.RightSlideUp, GestureId.RightSlideDown, GestureId.RightInward }.Count(id => ActionFor(id) != GestureAction.None),
        PadRegion.TopLeft => ActionFor(GestureId.TopLeftInward) != GestureAction.None ? 1 : 0,
        PadRegion.TopRight => ActionFor(GestureId.TopRightInward) != GestureAction.None ? 1 : 0,
        PadRegion.BottomLeft => ActionFor(GestureId.BottomLeftInward) != GestureAction.None ? 1 : 0,
        _ => ActionFor(GestureId.BottomRightInward) != GestureAction.None ? 1 : 0
    };

    private void Render()
    {
        if (handles.Any(h => h is null)) return;
        double left = Inset, top = Inset, width = BoardWidth, height = BoardHeight;
        Place(board, left, top, width, height);
        double lb = width * Ranges.Left, rb = width * Ranges.Right;
        double tb = height * Ranges.Top, bb = height * Ranges.Bottom;
        Place(bands[0], left, top, width, tb);
        Place(bands[1], left, top + height - bb, width, bb);
        Place(bands[2], left, top, lb, height);
        Place(bands[3], left + width - rb, top, rb, height);
        for (int i = 0; i < 4; i++)
        {
            bool right = i % 2 == 1, bottom = i >= 2;
            double bx = right ? rb : lb, by = bottom ? bb : tb;
            Place(bands[i + 4], left + (right ? width - bx : 0), top + (bottom ? height - by : 0), bx, by);
        }
        foreach (var band in bands) band.Fill = Brush(resizing ? (byte)75 : (byte)22, 76, 122, 235);
        bool compact = height < 200;
        double gap = compact ? 8 : 14, corner = compact ? 32 : 44, end = compact ? 44 : 68, rail = compact ? 28 : 36, sideRail = compact ? 44 : 54;
        Place(regions[0], left + end, top + gap, Math.Max(1, width - 2 * end), rail);
        Place(regions[1], left + end, top + height - gap - rail, Math.Max(1, width - 2 * end), rail);
        Place(regions[2], left + gap, top + end, sideRail, Math.Max(1, height - 2 * end));
        Place(regions[3], left + width - gap - sideRail, top + end, sideRail, Math.Max(1, height - 2 * end));
        for (int i = 0; i < 4; i++)
            Place(regions[i + 4], left + (i % 2 == 1 ? width - gap - corner : gap),
                top + (i >= 2 ? height - gap - corner : gap), corner, corner);
        for (int i = 0; i < regions.Length; i++)
        {
            var region = (PadRegion)i;
            UpdateRegionButtonContent(region, compact);
            regions[i].Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;
            bool selected = SelectedRegion == region;
            bool pulsed = highlightedRegion == region && DateTimeOffset.UtcNow < highlightUntil;
            bool hasBound = BoundCountForRegion(region) > 0;
            regions[i].Background = pulsed ? Brush(175, 42, 185, 145) :
                selected ? Brush(145, 76, 122, 235) :
                hasBound ? Brush(52, 76, 122, 235) : Brush(22, 110, 120, 145);
            regions[i].BorderBrush = pulsed ? Brush(255, 72, 225, 180) :
                selected ? Brush(255, 93, 146, 245) :
                hasBound ? Brush(145, 93, 146, 245) : Brush(70, 110, 120, 145);
        }
        centerTitle.FontSize = compact ? 15 : 22;
        centerSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Place(center, left + gap + sideRail + 6, top + gap + rail + 4,
            Math.Max(1, width - 2 * (gap + sideRail + 6)), Math.Max(1, height - 2 * (gap + rail + 4)));
        center.Visibility = resizing ? Visibility.Collapsed : Visibility.Visible;

        int totalBound = GestureCatalog.All.Count(s => ActionFor(s.Id) != GestureAction.None);
        var contacts = lastFrame?.Contacts.Where(c => c.Tip).Take(5).ToArray() ?? [];
        if (contacts.Length > 0)
        {
            string phaseText = lastPhase switch
            {
                GesturePhase.Tracking => "候选跟踪中",
                GesturePhase.Triggered => "手势已触发",
                GesturePhase.Pointer => "指针移动",
                GesturePhase.Cancelled => "已取消(待抬指)",
                _ => "采集中"
            };
            centerSubtitle.Text = $"{phaseText} · {contacts.Length} 指 ({contacts[0].NormalizedX:P0}, {contacts[0].NormalizedY:P0})";
        }
        else
        {
            centerSubtitle.Text = $"已绑定 {totalBound} / 16 项手势 · 点击四边或四角配置";
        }

        Place(handles[0], left + width / 2 - 32, top + tb - 10, 64, 20);
        Place(handles[1], left + width / 2 - 32, top + height - bb - 10, 64, 20);
        Place(handles[2], left + lb - 10, top + height / 2 - 32, 20, 64);
        Place(handles[3], left + width - rb - 10, top + height / 2 - 32, 20, 64);
        string[] badgeTexts = [$"上 {Ranges.Top:P1}", $"下 {Ranges.Bottom:P1}", $"左 {Ranges.Left:P1}", $"右 {Ranges.Right:P1}"];
        (double X, double Y)[] badgePos =
        [
            (left + width / 2 + 38, top + tb - 11),
            (left + width / 2 + 38, top + height - bb - 11),
            (left + lb + 14, top + height / 2 - 11),
            (left + width - rb - 68, top + height / 2 - 11)
        ];
        for (int i = 0; i < 4; i++)
        {
            handles[i].Visibility = resizing ? Visibility.Visible : Visibility.Collapsed;
            handleBadges[i].Visibility = resizing ? Visibility.Visible : Visibility.Collapsed;
            if (handleBadges[i].Child is TextBlock tbText) tbText.Text = badgeTexts[i];
            Canvas.SetLeft(handleBadges[i], badgePos[i].X);
            Canvas.SetTop(handleBadges[i], badgePos[i].Y);
        }

        rangeLabel.Text = $"识别范围：上 {Ranges.Top:P1}  ·  下 {Ranges.Bottom:P1}  ·  左 {Ranges.Left:P1}  ·  右 {Ranges.Right:P1}";

        if (lastOrigin is { } origin && contacts.Length == 1)
        {
            double ox = left + Math.Clamp(origin.X, 0, 1) * width;
            double oy = top + Math.Clamp(origin.Y, 0, 1) * height;
            double cx = left + Math.Clamp(contacts[0].NormalizedX, 0, 1) * width;
            double cy = top + Math.Clamp(contacts[0].NormalizedY, 0, 1) * height;
            originRing.Stroke = Brush(210, 72, 210, 175);
            originRing.Fill = Brush(45, 72, 210, 175);
            Canvas.SetLeft(originRing, ox - 9);
            Canvas.SetTop(originRing, oy - 9);
            originRing.Visibility = Visibility.Visible;
            trajectoryLine.X1 = ox; trajectoryLine.Y1 = oy;
            trajectoryLine.X2 = cx; trajectoryLine.Y2 = cy;
            trajectoryLine.Stroke = Brush(210, 72, 210, 175);
            trajectoryLine.Visibility = Visibility.Visible;
        }
        else
        {
            originRing.Visibility = Visibility.Collapsed;
            trajectoryLine.Visibility = Visibility.Collapsed;
        }

        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Visibility = ids[i].Visibility = i < contacts.Length ? Visibility.Visible : Visibility.Collapsed;
            if (i >= contacts.Length) continue;
            var c = contacts[i];
            double x = left + Math.Clamp(c.NormalizedX, 0, 1) * width - 13;
            double y = top + Math.Clamp(c.NormalizedY, 0, 1) * height - 13;
            dots[i].Fill = Brush(65, 64, 145, 230); dots[i].Stroke = Brush(220, 64, 145, 230);
            Canvas.SetLeft(dots[i], x); Canvas.SetTop(dots[i], y);
            Canvas.SetLeft(ids[i], x); Canvas.SetTop(ids[i], y); ids[i].Text = c.Id.ToString();
        }
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y);
        element.Width = width; element.Height = height;
    }
}
