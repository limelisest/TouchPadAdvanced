using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace TouchPadAdvanced;

// Positions the preview and a single region editor inside one clipped viewport.
// Region controls remain alive when switching; only their presentation changes.
internal sealed class PadWorkspace : Grid
{
    private const double MarginSize = 16, Gap = 20, PreviewPeek = 64;
    private readonly PadPreview preview;
    private readonly IReadOnlyDictionary<PadRegion, FrameworkElement> editors;
    private readonly Canvas stage = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TranslateTransform offset = new();
    private readonly ContentControl editorHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer editorScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock title = new() { FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock modeHint = new() { FontSize = 13, Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button reset = new() { Content = "返回全貌", Visibility = Visibility.Collapsed, Padding = new Thickness(12, 5, 12, 5) };
    private readonly ToggleButton resize = new() { Content = "调整范围", Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button sensitivity = new() { Content = "灵敏度与设置", HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 5, 12, 5) };
    private readonly FrameworkElement settingsEditor;
    private readonly Border panel;
    private bool settingsOpen;
    internal bool IsSettingsOpen => settingsOpen;
    private Storyboard? movement;
    private Point destination;

    internal PadWorkspace(PadPreview preview, IReadOnlyDictionary<PadRegion, FrameworkElement> editors, FrameworkElement settingsEditor)
    {
        this.preview = preview;
        this.editors = editors;
        this.settingsEditor = settingsEditor;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        stage.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var toolbar = new Grid { ColumnSpacing = 10, Margin = new Thickness(4, 0, 4, 0) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(modeHint);
        toolbar.Children.Add(reset); Grid.SetColumn(reset, 1);
        var tools = new StackPanel { Spacing = 5 };
        resize.HorizontalAlignment = HorizontalAlignment.Stretch;
        tools.Children.Add(resize); tools.Children.Add(sensitivity);
        toolbar.Children.Add(tools); Grid.SetColumn(tools, 2);
        Children.Add(toolbar);
        Grid.SetRow(stage, 1);
        Children.Add(stage);
        preview.RenderTransform = offset;
        stage.Children.Add(preview);

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(title);
        var close = new Button { Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }, Padding = new Thickness(8) };
        ToolTipService.SetToolTip(close, "返回全貌");
        close.Click += (_, _) => ClearSelection();
        heading.Children.Add(close); Grid.SetColumn(close, 1);
        editorScroll.Content = editorHost;
        var content = new Grid { RowSpacing = 14 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(heading);
        Grid.SetRow(editorScroll, 1); content.Children.Add(editorScroll);
        panel = new Border
        {
            Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed
        };
        stage.Children.Add(panel);
        preview.RegionSelected += ShowRegion;
        reset.Click += (_, _) => ClearSelection();
        sensitivity.Click += (_, _) => { if (settingsOpen) ClearSelection(); else ShowSettings(); };
        resize.Checked += (_, _) =>
        {
            preview.SelectRegion(null);
            preview.IsResizing = true;
            UpdateModeHint();
        };
        resize.Unchecked += (_, _) =>
        {
            preview.IsResizing = false;
            UpdateModeHint();
        };
        stage.SizeChanged += (_, _) => ArrangeWorkspace(false);
        panel.SizeChanged += (_, _) => ArrangeWorkspace(true);
        Loaded += (_, _) => ArrangeWorkspace(false);
        UpdateModeHint();
    }

    private void UpdateModeHint()
    {
        modeHint.Text = preview.IsResizing
            ? "范围调整模式 · 拖动蓝色边界线或按方向键微调四边触发宽度"
            : settingsOpen
                ? "全局设置 · 调整触发阈值、步长、打字防误触与系统托盘选项"
                : preview.SelectedRegion is { } r
                    ? $"正在配置：{PadPreview.RegionName(r)} · 点击空白处或中央返回全貌"
                    : "全貌视图 · 点击触控板四边或四角绑定动作";
    }

    private void ShowRegion(PadRegion? region)
    {
        settingsOpen = false;
        if (region is { } selected)
        {
            title.Text = PadPreview.RegionName(selected);
            editorHost.Content = editors[selected];
            panel.Visibility = Visibility.Visible;
            reset.Visibility = Visibility.Visible;
        }
        else
        {
            editorHost.Content = null;
            panel.Visibility = Visibility.Collapsed;
            reset.Visibility = Visibility.Collapsed;
        }
        UpdateModeHint();
        ArrangeWorkspace(true);
    }

    internal void ShowSettings()
    {
        resize.IsChecked = false;
        preview.SelectRegion(null);
        settingsOpen = true;
        title.Text = "灵敏度与设置";
        editorHost.Content = settingsEditor;
        panel.Visibility = Visibility.Visible;
        reset.Visibility = Visibility.Visible;
        UpdateModeHint();
        ArrangeWorkspace(true);
    }

    internal void ClearSelection() => preview.SelectRegion(null);

    // Taps on editor contents or interactive controls must not dismiss their own editor.
    internal void DismissSelectionFrom(DependencyObject? source)
    {
        for (var element = source; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == panel || element is ButtonBase or Thumb or TextBox or NumberBox or
                ComboBox or SelectorItem or ToggleSwitch or NavigationViewItem or ScrollBar) return;
            if (element == this) break;
        }
        ClearSelection();
    }

    private void ArrangeWorkspace(bool animate)
    {
        double width = stage.ActualWidth, height = stage.ActualHeight;
        if (width <= 0 || height <= 0) return;
        stage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        double ratio = Math.Clamp(preview.AspectRatio, 1.35, 1.85);
        double defaultH = Math.Clamp(height - 32, 210, 340);
        double defaultW = Math.Clamp(defaultH * ratio, 360, Math.Max(360, Math.Min(560, width - 48)));
        preview.Width = defaultW;
        preview.Height = defaultH;
        double x = (width - preview.Width) / 2, y = (height - preview.Height) / 2;

        if (settingsOpen || preview.SelectedRegion is not null)
        {
            var region = preview.SelectedRegion ?? PadRegion.Right;
            bool vertical = !settingsOpen && region is PadRegion.Top or PadRegion.Bottom;
            panel.Width = Math.Min(settingsOpen ? 410 : vertical ? 410 : 330, width - 2 * MarginSize);
            panel.MaxHeight = Math.Max(100, height - 2 * MarginSize - (vertical ? PreviewPeek : 0));
            panel.Measure(new Size(panel.Width, panel.MaxHeight));
            double panelHeight = Math.Min(panel.MaxHeight, panel.DesiredSize.Height);

            if (vertical && height > 380)
            {
                double availH = height - panelHeight - Gap - 2 * MarginSize;
                preview.Height = Math.Clamp(availH, 175, 320);
                preview.Width = Math.Clamp(preview.Height * ratio, 340, Math.Min(520, width - 48));
            }
            else if (!vertical && width > 760)
            {
                double availW = width - panel.Width - Gap - 2 * MarginSize;
                preview.Width = Math.Clamp(availW, 360, 520);
                preview.Height = Math.Clamp(preview.Width / ratio, 210, Math.Min(330, height - 32));
            }

            x = (width - preview.Width) / 2;
            y = (height - preview.Height) / 2;
            double panelX = (width - panel.Width) / 2, panelY = (height - panelHeight) / 2;

            switch (region)
            {
                case PadRegion.Top:
                {
                    double totalH = panelHeight + Gap + preview.Height;
                    panelY = height >= totalH + 2 * MarginSize
                        ? Math.Max(MarginSize, (height - totalH) / 2)
                        : MarginSize;
                    y = panelY + panelHeight + Gap;
                    break;
                }
                case PadRegion.Bottom:
                {
                    double totalH = preview.Height + Gap + panelHeight;
                    panelY = height >= totalH + 2 * MarginSize
                        ? Math.Min(height - MarginSize - panelHeight, (height + totalH) / 2 - panelHeight)
                        : height - MarginSize - panelHeight;
                    y = panelY - Gap - preview.Height;
                    break;
                }
                case PadRegion.Left:
                case PadRegion.TopLeft:
                case PadRegion.BottomLeft:
                {
                    double totalW = panel.Width + Gap + preview.Width;
                    panelX = width >= totalW + 2 * MarginSize
                        ? Math.Max(MarginSize, (width - totalW) / 2)
                        : MarginSize;
                    x = panelX + panel.Width + Gap;
                    break;
                }
                default:
                {
                    double totalW = preview.Width + Gap + panel.Width;
                    panelX = width >= totalW + 2 * MarginSize
                        ? Math.Min(width - MarginSize - panel.Width, (width + totalW) / 2 - panel.Width)
                        : width - MarginSize - panel.Width;
                    x = panelX - Gap - preview.Width;
                    break;
                }
            }
            if (region is PadRegion.TopLeft or PadRegion.TopRight)
                panelY = Math.Max(MarginSize, Math.Min(y, height - MarginSize - panelHeight));
            if (region is PadRegion.BottomLeft or PadRegion.BottomRight)
                panelY = Math.Min(height - MarginSize - panelHeight, Math.Max(MarginSize, y + preview.Height - panelHeight));
            Canvas.SetLeft(panel, panelX);
            Canvas.SetTop(panel, panelY);
        }
        MovePreview(new Point(x, y), animate);
    }

    private void MovePreview(Point target, bool animate)
    {
        if (animate && Math.Abs(target.X - destination.X) < .1 && Math.Abs(target.Y - destination.Y) < .1) return;
        double fromX = offset.X, fromY = offset.Y;
        movement?.Stop();
        destination = target;
        offset.X = target.X; offset.Y = target.Y;
        if (!animate) return;
        movement = new Storyboard();
        foreach (var (property, from, to) in new[] { ("X", fromX, target.X), ("Y", fromY, target.Y) })
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, offset);
            Storyboard.SetTargetProperty(animation, property);
            movement.Children.Add(animation);
        }
        movement.Begin();
    }

    internal void SetResizeMode(bool enabled) => resize.IsChecked = enabled;

    internal object InspectLayout()
    {
        // Complete layout and stop motion at its target for deterministic integration assertions.
        UpdateLayout();
        ArrangeWorkspace(false);
        return new
        {
            region = settingsOpen ? "Settings" : preview.SelectedRegion?.ToString(),
            previewX = destination.X, previewY = destination.Y,
            previewWidth = preview.Width, previewHeight = preview.Height,
            viewportWidth = stage.ActualWidth, viewportHeight = stage.ActualHeight,
            panelVisible = panel.Visibility == Visibility.Visible,
            settingsButtonBelowResize = sensitivity.TransformToVisual(this).TransformPoint(new Point()).Y > resize.TransformToVisual(this).TransformPoint(new Point()).Y,
            panelX = panel.Visibility == Visibility.Visible ? Canvas.GetLeft(panel) : 0,
            panelY = panel.Visibility == Visibility.Visible ? Canvas.GetTop(panel) : 0,
            panelWidth = panel.ActualWidth, panelHeight = panel.ActualHeight,
            editorViewportHeight = editorScroll.ViewportHeight, editorScrollableHeight = editorScroll.ScrollableHeight,
            editorControls = editorHost.Content is StackPanel stack ? stack.Children.Count : 0
        };
    }
}
