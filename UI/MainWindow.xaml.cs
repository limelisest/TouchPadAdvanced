using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace TouchPadAdvanced;

public sealed partial class MainWindow : Window
{
    private GestureInputHost? inputHost;
    private RawInputSource? source;
    private GestureRuntime? runtime;
    private PointerCaptureController? pointerCapture;
    private TrayIcon? trayIcon;
    private readonly bool smokeMode;
    private bool exiting;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly PadPreview preview = new();
    private readonly ToggleSwitch enabled = new() { Header = "启用手势", OnContent = "已启用", OffContent = "已关闭", MinWidth = 0 };
    private readonly ToggleSwitch observe = new() { Header = "观察模式", OnContent = "只识别", OffContent = "识别并执行", MinWidth = 0 };
    private readonly ToggleSwitch capture = new() { Header = "拦截手势移动", OnContent = "锁定指针", OffContent = "保留指针", MinWidth = 0 };
    private readonly ToggleSwitch typingGuardToggle = new() { Header = "打字防误触", OnContent = "已开启", OffContent = "已关闭", MinWidth = 0 };
    private readonly ToggleSwitch minimizeToTray = new() { Header = "关闭窗口时", OnContent = "最小化托盘", OffContent = "直接退出", MinWidth = 0 };
    private readonly NumberBox distance = Number("触发距离 %", 1, 30);
    private readonly NumberBox selectionDistance = Number("切换步长 %", 2, 30);
    private readonly NumberBox repeat = Number("调节步长 %", 1, 20);
    private readonly List<(GestureId Id, ComboBox Action, TextBox Shortcut)> bindings = [];
    private readonly ComboBox devicePicker = new() { Header = "当前触控板", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock deviceStats = Text("", 13);
    private readonly TextBlock recentActivityText = Text("尚无手势触发记录。", 13);
    private Grid? gesturesPage;
    private StackPanel? devicePage;
    private PadWorkspace? workspace;
    private readonly ToggleSwitch startup = new() { Header = "开机自启", OnContent = "已开启", OffContent = "已关闭", MinWidth = 0 };
    private readonly StartupRegistration startupRegistration = new(System.IO.Path.Combine(AppContext.BaseDirectory, "TouchPadAdvanced.exe"));
    private bool updatingStartup;
    private int deviceVersion = -1;
    private long actionStatusVersion = -1;
    private string lastTriggerSummary = "";
    private bool refreshingDevices, loadingSettings;

    public MainWindow(bool observeOnly = false)
    {
        smokeMode = observeOnly;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Title = "TouchPad Advanced";
        string iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var area = display.WorkArea;
        int width = Math.Min(1320, area.Width - 40), height = Math.Min(900, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
        try
        {
            GestureSettings settings;
            try { settings = GestureSettingsStore.Load(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                settings = new GestureSettings { Enabled = false };
                ShowNotice("设置未能加载", "原文件已保留，请检查设置后保存：" + ex.Message, InfoBarSeverity.Warning);
            }
            if (observeOnly) settings = settings with { ObserveOnly = true };
            inputHost = new GestureInputHost(settings);
            source = inputHost.Source;
            runtime = inputHost.Runtime;
            pointerCapture = inputHost.Capture;
            BuildPages();
            LoadSettings(settings);
            RefreshStartupRegistration();
            Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) RefreshStartupRegistration(); };
            RefreshDevices();
            Navigation.SelectedItem = Navigation.MenuItems[0];
            trayIcon = new TrayIcon(
                onOpen: () => DispatcherQueue.TryEnqueue(RestoreWindow),
                onTogglePause: () => DispatcherQueue.TryEnqueue(() => TogglePause(this, new RoutedEventArgs())),
                onToggleObserve: () => DispatcherQueue.TryEnqueue(ToggleObserveFromTray),
                onExit: () => DispatcherQueue.TryEnqueue(ExitApplication),
                getState: () => (runtime?.Paused ?? false, runtime?.Settings.ObserveOnly ?? false));
            AppWindow.Closing += (_, args) =>
            {
                if (!smokeMode && !exiting && minimizeToTray.IsOn && trayIcon?.IsInstalled == true)
                {
                    args.Cancel = true;
                    AppWindow.Hide();
                }
            };
            timer.Tick += (_, _) => UpdateState();
            timer.Start();
            Closed += (_, _) => { timer.Stop(); trayIcon?.Dispose(); inputHost.Dispose(); };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            trayIcon?.Dispose();
            inputHost?.Dispose();
            inputHost = null; source = null; runtime = null; pointerCapture = null;
            PauseButton.IsEnabled = false;
            ShowNotice("触控板未能启动", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RestoreWindow()
    {
        AppWindow.Show();
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != 0)
        {
            ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
        }
        Activate();
    }

    private void ToggleObserveFromTray()
    {
        if (runtime is null) return;
        observe.IsOn = !observe.IsOn;
        ApplySettings();
        UpdateState();
    }

    internal void ExitApplication()
    {
        exiting = true;
        Close();
    }

    internal void EnterStartupMode()
    {
        if (minimizeToTray.IsOn && trayIcon?.IsInstalled == true)
            AppWindow.Hide();
        else if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Minimize();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    private static NumberBox Number(string header, double min, double max) => new()
    {
        Header = header, Minimum = min, Maximum = max, SmallChange = .5, LargeChange = 1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static TextBlock Text(string value, double size = 14) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap
    };

    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(12),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1)
    };

    private void BuildPages()
    {
        gesturesPage = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        enabled.Toggled += (_, _) => MarkDirty();
        observe.Toggled += (_, _) => MarkDirty();
        capture.Toggled += (_, _) => MarkDirty();
        typingGuardToggle.Toggled += (_, _) => MarkDirty();
        minimizeToTray.Toggled += (_, _) => MarkDirty();
        distance.ValueChanged += (_, _) => MarkDirty();
        repeat.ValueChanged += (_, _) => MarkDirty();
        selectionDistance.ValueChanged += (_, _) => MarkDirty();
        preview.RangesChanged += MarkDirty;
        startup.Toggled += ChangeStartupRegistration;
        var regionEditors = new Dictionary<PadRegion, FrameworkElement>
        {
            [PadRegion.Top] = CreateRegionEditor([GestureId.TopSlideLeft, GestureId.TopSlideRight, GestureId.TopInward]),
            [PadRegion.Bottom] = CreateRegionEditor([GestureId.BottomSlideLeft, GestureId.BottomSlideRight, GestureId.BottomInward]),
            [PadRegion.Left] = CreateRegionEditor([GestureId.LeftSlideUp, GestureId.LeftSlideDown, GestureId.LeftInward]),
            [PadRegion.Right] = CreateRegionEditor([GestureId.RightSlideUp, GestureId.RightSlideDown, GestureId.RightInward]),
            [PadRegion.TopLeft] = CreateRegionEditor([GestureId.TopLeftInward]),
            [PadRegion.TopRight] = CreateRegionEditor([GestureId.TopRightInward]),
            [PadRegion.BottomLeft] = CreateRegionEditor([GestureId.BottomLeftInward]),
            [PadRegion.BottomRight] = CreateRegionEditor([GestureId.BottomRightInward])
        };
        var parameters = new StackPanel { Spacing = 10 };
        var switchesGrid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        switchesGrid.ColumnDefinitions.Add(new ColumnDefinition());
        switchesGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 3; r++) switchesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ToggleSwitch[] allSwitches = [enabled, capture, observe, typingGuardToggle, startup, minimizeToTray];
        for (int i = 0; i < allSwitches.Length; i++)
        {
            Grid.SetRow(allSwitches[i], i / 2);
            Grid.SetColumn(allSwitches[i], i % 2);
            switchesGrid.Children.Add(allSwitches[i]);
        }
        parameters.Children.Add(switchesGrid);

        var numbersGrid = new Grid { ColumnSpacing = 8 };
        numbersGrid.ColumnDefinitions.Add(new ColumnDefinition());
        numbersGrid.ColumnDefinitions.Add(new ColumnDefinition());
        numbersGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(distance, 0);
        Grid.SetColumn(repeat, 1);
        Grid.SetColumn(selectionDistance, 2);
        numbersGrid.Children.Add(distance);
        numbersGrid.Children.Add(repeat);
        numbersGrid.Children.Add(selectionDistance);
        parameters.Children.Add(numbersGrid);

        var reset = new Button { Content = "恢复默认预设", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) =>
        {
            LoadSettings(new GestureSettings()); MarkDirty();
        };
        parameters.Children.Add(reset);
        workspace = new PadWorkspace(preview, regionEditors, parameters);
        gesturesPage.Children.Add(workspace);
        ((UIElement)Content).AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, args) =>
            workspace.DismissSelectionFrom(args.OriginalSource as DependencyObject)), true);
        void FitGesturePage() => gesturesPage.Height = Math.Max(440, PageScroll.ActualHeight);
        PageScroll.SizeChanged += (_, _) => FitGesturePage();
        FitGesturePage();

        devicePage = new StackPanel { Spacing = 14 };
        var selection = new Grid { ColumnSpacing = 12 };
        selection.ColumnDefinitions.Add(new ColumnDefinition());
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        selection.Children.Add(devicePicker);
        var refresh = new Button { Content = "刷新设备", VerticalAlignment = VerticalAlignment.Bottom };
        refresh.Click += (_, _) =>
        {
            try { source!.RefreshDevices(); deviceVersion = -1; RefreshDevices(); }
            catch (System.ComponentModel.Win32Exception ex) { ShowNotice("设备刷新失败", ex.Message, InfoBarSeverity.Error); }
        };
        Grid.SetColumn(refresh, 1);
        selection.Children.Add(refresh);
        devicePicker.SelectionChanged += (_, _) =>
        {
            if (refreshingDevices) return;
            runtime!.SelectDevice((devicePicker.SelectedItem as DeviceOption)?.Device);
        };
        devicePage.Children.Add(Card(selection));
        devicePage.Children.Add(Card(deviceStats));
        var activityCard = new StackPanel { Spacing = 6 };
        activityCard.Children.Add(new TextBlock { Text = "最近手势触发记录", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
        activityCard.Children.Add(recentActivityText);
        devicePage.Children.Add(Card(activityCard));
        var actionsBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var export = new Button { Content = "导出诊断文件" };
        export.Click += ExportDiagnostics;
        actionsBar.Children.Add(export);
        var hideBtn = new Button { Content = "最小化到系统托盘" };
        hideBtn.Click += (_, _) => { if (trayIcon?.IsInstalled == true) AppWindow.Hide(); };
        actionsBar.Children.Add(hideBtn);
        devicePage.Children.Add(actionsBar);
    }

    private StackPanel CreateRegionEditor(GestureId[] ids)
    {
        var content = new StackPanel { Spacing = 14 };
        foreach (var id in ids)
        {
            var row = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var spec = GestureCatalog.Get(id);
            string direction = spec.Corner is not null ? "斜向内滑" : spec.Inward ? "向内滑" :
                spec.Edge is PadEdge.Top or PadEdge.Bottom ? (spec.Positive ? "向右滑" : "向左滑") :
                spec.Positive ? "向下滑" : "向上滑";
            row.Children.Add(new TextBlock { Text = direction, VerticalAlignment = VerticalAlignment.Center });
            var action = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var value in Enum.GetValues<GestureAction>().Where(a =>
                a is not (GestureAction.AppSwitcher or GestureAction.PreviousTask)))
            {
                if (value == GestureAction.SwitchApp)
                    action.Items.Add(new ComboBoxItem { Content = ActionName(GestureAction.PreviousTask), Tag = GestureAction.PreviousTask });
                action.Items.Add(new ComboBoxItem { Content = ActionName(value), Tag = value });
            }
            AutomationProperties.SetName(action, GestureName(id) + "动作");
            var shortcut = new TextBox
            {
                PlaceholderText = "按组合键或输入 Ctrl+Shift+T", HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(shortcut, GestureName(id) + "自定义快捷键");
            void FocusShortcut()
            {
                if (loadingSettings || shortcut.Visibility != Visibility.Visible || action.IsDropDownOpen) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (shortcut.Visibility != Visibility.Visible || !shortcut.IsLoaded) return;
                    shortcut.UpdateLayout();
                    shortcut.Focus(FocusState.Programmatic);
                    shortcut.StartBringIntoView();
                });
            }
            action.DropDownClosed += (_, _) => FocusShortcut();
            shortcut.PreviewKeyDown += RecordShortcut;
            action.SelectionChanged += (_, _) =>
            {
                shortcut.Visibility = (GestureAction?)((action.SelectedItem as ComboBoxItem)?.Tag) == GestureAction.Shortcut
                    ? Visibility.Visible : Visibility.Collapsed;
                FocusShortcut();
                SyncDraftToPreview();
                MarkDirty();
            };
            shortcut.TextChanged += (_, _) => MarkDirty();
            Grid.SetColumn(action, 1); Grid.SetRow(shortcut, 1); Grid.SetColumn(shortcut, 1);
            row.Children.Add(action); row.Children.Add(shortcut);
            content.Children.Add(row);
            bindings.Add((id, action, shortcut));
        }
        return content;
    }

    private void SyncDraftToPreview()
    {
        preview.SetDraftBindings(bindings
            .Where(b => b.Action.SelectedItem is ComboBoxItem)
            .Select(b => (b.Id, (GestureAction)((ComboBoxItem)b.Action.SelectedItem).Tag)));
    }

    private static void RecordShortcut(object sender, KeyRoutedEventArgs args)
    {
        var box = (TextBox)sender;
        var modifiers = ShortcutModifiers.None;
        bool Held(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
                Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (Held(Windows.System.VirtualKey.Control)) modifiers |= ShortcutModifiers.Ctrl;
        if (Held(Windows.System.VirtualKey.Menu)) modifiers |= ShortcutModifiers.Alt;
        if (Held(Windows.System.VirtualKey.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (Held(Windows.System.VirtualKey.LeftWindows) || Held(Windows.System.VirtualKey.RightWindows))
            modifiers |= ShortcutModifiers.Win;
        if (ShortcutKeys.TryFormatRecorded((ushort)args.Key, modifiers) is not { } text) return;
        box.Text = text;
        box.SelectAll();
        args.Handled = true;
    }

    private void LoadSettings(GestureSettings settings)
    {
        loadingSettings = true;
        try
        {
            enabled.IsOn = settings.Enabled;
            observe.IsOn = settings.ObserveOnly;
            capture.IsOn = settings.CapturePointer;
            typingGuardToggle.IsOn = settings.SuppressWhileTyping;
            minimizeToTray.IsOn = settings.MinimizeToTray;
            preview.SetRanges(new EdgeRecognitionRanges(settings.GetEdgeWidth(PadEdge.Top),
                settings.GetEdgeWidth(PadEdge.Bottom), settings.GetEdgeWidth(PadEdge.Left), settings.GetEdgeWidth(PadEdge.Right)));
            distance.Value = settings.TriggerDistance * 100;
            repeat.Value = settings.RepeatDistance * 100;
            selectionDistance.Value = settings.SelectionDistance * 100;
            foreach (var row in bindings)
            {
                var binding = settings.Binding(row.Id);
                var action = binding.Action == GestureAction.AppSwitcher ? GestureAction.SwitchApp : binding.Action;
                row.Action.SelectedItem = row.Action.Items.Cast<ComboBoxItem>().Single(item => (GestureAction)item.Tag == action);
                row.Shortcut.Text = binding.Shortcut;
            }
            SyncDraftToPreview();
        }
        finally { loadingSettings = false; }
    }

    private void MarkDirty()
    {
        if (loadingSettings) return;
        SaveButton.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
        SaveButton.Content = "保存并生效";
    }

    private void SaveChanges(object sender, RoutedEventArgs args) => ApplySettings();

    private bool ApplySettings(string? path = null)
    {
        try
        {
            var settings = new GestureSettings
            {
                Enabled = enabled.IsOn,
                ObserveOnly = observe.IsOn,
                CapturePointer = capture.IsOn,
                SuppressWhileTyping = typingGuardToggle.IsOn,
                MinimizeToTray = minimizeToTray.IsOn,
                EdgeRanges = preview.Ranges,
                TriggerDistance = distance.Value / 100,
                RepeatDistance = repeat.Value / 100,
                SelectionDistance = selectionDistance.Value / 100,
                Bindings = bindings.Select(r => new GestureBinding(r.Id,
                    (GestureAction)((ComboBoxItem)r.Action.SelectedItem).Tag, r.Shortcut.Text.Trim())).ToArray()
            };
            settings.Validate();
            GestureSettingsStore.Save(settings, path);
            runtime!.Apply(settings);
            SyncDraftToPreview();
            Notice.IsOpen = false;
            SaveButton.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            SaveButton.Content = "已保存";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowNotice("设置未生效", ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private void Navigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (runtime is null || args.SelectedItem is not NavigationViewItem item) return;
        bool device = item.Tag?.ToString() == "device";
        PageHost.Children.Clear();
        PageHost.Children.Add(device ? devicePage! : gesturesPage!);
        PageTitle.Text = device ? "设备与状态" : "手势配置";
    }

    private void RefreshStartupRegistration()
    {
        updatingStartup = true;
        try
        {
            startup.IsOn = startupRegistration.IsEnabled;
            startup.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            startup.IsEnabled = false;
            ShowNotice("无法读取自启设置", ex.Message, InfoBarSeverity.Error);
        }
        finally { updatingStartup = false; }
    }

    private void ChangeStartupRegistration(object sender, RoutedEventArgs args)
    {
        if (updatingStartup) return;
        try { startupRegistration.SetEnabled(startup.IsOn); Notice.IsOpen = false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            updatingStartup = true;
            startup.IsOn = !startup.IsOn;
            updatingStartup = false;
            ShowNotice("自启设置未保存", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void TogglePause(object sender, RoutedEventArgs args)
    {
        if (runtime is null) return;
        runtime.SetPaused(!runtime.Paused);
        UpdateState();
    }

    private void RefreshDevices()
    {
        if (source is null || runtime is null || deviceVersion == source.Devices.Count) return;
        deviceVersion = source.Devices.Count;
        refreshingDevices = true;
        devicePicker.Items.Clear();
        foreach (var device in source.Devices)
            devicePicker.Items.Add(new DeviceOption(device, device.Label + (device.CanDecodeContacts ? "" : " · 待解析")));
        devicePicker.DisplayMemberPath = nameof(DeviceOption.Label);
        devicePicker.SelectedItem = devicePicker.Items.Cast<DeviceOption>().FirstOrDefault(o => o.Device == runtime.Device)
            ?? devicePicker.Items.Cast<DeviceOption>().FirstOrDefault(o => o.Device.CanDecodeContacts && o.Device.Connected);
        runtime.SelectDevice((devicePicker.SelectedItem as DeviceOption)?.Device);
        refreshingDevices = false;
    }

    private void UpdateState()
    {
        if (source is null || runtime is null) return;
        if (inputHost?.IsRunning == false) { RuntimeStatus.Text = "输入线程已停止，请重新启动应用。"; return; }
        RefreshDevices();
        if (actionStatusVersion != runtime.ActivityVersion)
        {
            actionStatusVersion = runtime.ActivityVersion;
            var latest = runtime.Activities.LastOrDefault();
            if (latest is not null)
            {
                if (!latest.Succeeded && !latest.Observed)
                    ShowNotice("动作未完成", latest.Detail, InfoBarSeverity.Error);
                else
                {
                    preview.HighlightGesture(latest.Gesture);
                    lastTriggerSummary = $"最近触发：{GestureName(latest.Gesture)} → {ActionName(latest.Action)}";
                }
            }
            var lines = runtime.Activities.Reverse().Take(8).Select(a =>
                $"[{a.At.ToLocalTime():HH:mm:ss}] {GestureName(a.Gesture)} → {ActionName(a.Action)} " +
                (a.Steps > 1 ? $"×{a.Steps} " : "") +
                (a.Observed ? "(观察模式)" : a.Succeeded ? "(已执行)" : $"(失败：{a.Detail})")).ToArray();
            recentActivityText.Text = lines.Length > 0 ? string.Join("\n", lines) : "尚无手势触发记录。";
        }
        PauseButton.Content = runtime.Paused ? "继续手势" : "暂停手势";
        var device = runtime.Device;
        string baseStatus = runtime.Paused || !runtime.Settings.Enabled ? "手势已暂停" :
            device is not { Connected: true, CanDecodeContacts: true } ? "等待有效触控板" :
            runtime.Settings.ObserveOnly ? "观察模式" : "手势已启用";
        if (pointerCapture?.IsCapturing == true) baseStatus += " · 已锁定指针";
        if (lastTriggerSummary.Length > 0) baseStatus += "  |  " + lastTriggerSummary;
        RuntimeStatus.Text = baseStatus;
        trayIcon?.UpdateTooltip($"TouchPad Advanced - {(runtime.Paused ? "已暂停" : runtime.Settings.ObserveOnly ? "观察模式" : "运行中")}");

        if (device is not null) preview.AspectRatio = device.AspectRatio;
        preview.Update(device is { Connected: true } ? device.Frame : null, runtime.Settings, runtime.ActiveOrigin, runtime.Phase);
        if (device is not null)
        {
            deviceStats.Text =
                $"连接状态：{(device.Connected ? "已连接" : "已断开")}  ·  比例：{device.AspectRatio:F2}:1  ·  报文长度：{device.InputReportLength} 字节\n" +
                $"当前触点：{device.Frame?.ActiveCount ?? 0} 指（历史峰值：{device.MaxActiveContacts} 指）  ·  累计帧数：{device.FrameCount}\n" +
                $"指针钩子：{(pointerCapture?.IsInstalled == true ? "已启用" : "未安装")}（已拦截移动：{pointerCapture?.SuppressedEvents ?? 0} 次）  ·  " +
                $"紧急热键 Ctrl+Alt+F8：{(pointerCapture?.EmergencyHotkeyAvailable == true ? "可用" : "未注册")}";
            if (device.LastError is not null) deviceStats.Text += "\n错误：" + device.LastError;
        }
    }

    private async void ExportDiagnostics(object sender, RoutedEventArgs args)
    {
        if (source is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = $"touchpad-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add("JSON 诊断文件", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            Diagnostics.Save(source, file.Path, runtime, pointerCapture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowNotice("导出失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
    }

    private static string GestureName(GestureId id) => id switch
    {
        GestureId.TopInward => "上边缘向内 ↓", GestureId.BottomInward => "下边缘向内 ↑",
        GestureId.LeftInward => "左边缘向内 →", GestureId.RightInward => "右边缘向内 ←",
        GestureId.TopSlideLeft => "上边缘左滑", GestureId.TopSlideRight => "上边缘右滑",
        GestureId.BottomSlideLeft => "下边缘左滑", GestureId.BottomSlideRight => "下边缘右滑",
        GestureId.LeftSlideUp => "左边缘上滑", GestureId.LeftSlideDown => "左边缘下滑",
        GestureId.RightSlideUp => "右边缘上滑", GestureId.RightSlideDown => "右边缘下滑",
        GestureId.TopLeftInward => "左上角 ↘", GestureId.TopRightInward => "右上角 ↙",
        GestureId.BottomLeftInward => "左下角 ↗", GestureId.BottomRightInward => "右下角 ↖",
        _ => id.ToString()
    };

    private static string ActionName(GestureAction action) => action switch
    {
        GestureAction.None => "不绑定", GestureAction.Back => "后退 · 鼠标侧键 1",
        GestureAction.Forward => "前进 · 鼠标侧键 2", GestureAction.VolumeUp => "增大音量",
        GestureAction.VolumeDown => "减小音量", GestureAction.Mute => "切换静音",
        GestureAction.PlayPause => "播放 / 暂停", GestureAction.PreviousTrack => "上一曲",
        GestureAction.NextTrack => "下一曲", GestureAction.Shortcut => "自定义快捷键",
        GestureAction.SwitchApp => "下一个任务", GestureAction.PreviousTask => "上一个任务",
        GestureAction.AppSwitcher => "下一个任务",
        GestureAction.TaskView => "任务视图", GestureAction.StartMenu => "打开开始菜单",
        GestureAction.ShowDesktop => "显示 / 还原桌面", GestureAction.PreviousDesktop => "上一个虚拟桌面",
        GestureAction.NextDesktop => "下一个虚拟桌面", GestureAction.QuickSettings => "打开快速设置",
        GestureAction.Notifications => "打开通知中心", GestureAction.FileExplorer => "打开资源管理器",
        GestureAction.ScrollUp => "向上滚动 · 滚轮", GestureAction.ScrollDown => "向下滚动 · 滚轮",
        GestureAction.ScrollLeft => "向左滚动 · 水平滚轮", GestureAction.ScrollRight => "向右滚动 · 水平滚轮",
        GestureAction.BrightnessUp => "增大屏幕亮度", GestureAction.BrightnessDown => "减小屏幕亮度",
        _ => action.ToString()
    };

    private sealed record DeviceOption(TouchpadDevice Device, string Label);
}
