using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TouchPadAdvanced;

// Integration checks run only through --smoke; persistence uses an isolated temporary file.
public sealed partial class MainWindow
{
    internal object SmokeState(object shortcutInput)
    {
        var spatialEditor = ExerciseSpatialEditor();
        var settingsInteraction = ExerciseSettingsEditor();
        var editor = ExerciseEditor();
        var startupOption = InspectStartupOption();
        ((FrameworkElement)Content).UpdateLayout();
        return new
        {
            initialized = runtime is not null && gesturesPage is not null && PageHost.Children.Count == 1 && timer.IsEnabled,
            nativeWindow = WinRT.Interop.WindowNative.GetWindowHandle(this).ToString(),
            width = AppWindow.Size.Width, height = AppWindow.Size.Height,
            bindingControls = bindings.Count,
            edgeRangeControls = preview.HandleCount,
            regionButtons = preview.RegionCount,
            spatialEditor,
            shortcutInput,
            settingsInteraction,
            startupOption,
            saveButtonInFixedFooter = SaveButton.Parent == FooterBar && FooterBar.Parent == PageScroll.Parent && Grid.GetRow(FooterBar) > Grid.GetRow(PageScroll),
            editor,
            windowsActionOptions = bindings.Count == 0 ? 0 : bindings[0].Action.Items.Count,
            contentWidth = ((FrameworkElement)Content).ActualWidth,
            contentHeight = ((FrameworkElement)Content).ActualHeight,
            previewWidth = preview.ActualWidth,
            deviceCount = source?.Devices.Count,
            selectedDevice = runtime?.Device?.Path,
            captureHookInstalled = pointerCapture?.IsInstalled,
            inputThreadRunning = inputHost?.IsRunning,
            sourceErrors = source?.Errors.ToArray()
        };
    }
    private object InspectStartupOption()
    {
        workspace!.ShowSettings();
        ((FrameworkElement)Content).UpdateLayout();
        var state = new
        {
            controlAvailable = startup.Parent is not null && startup.IsEnabled && startup.ActualWidth > 0,
            matchesRegistry = startup.IsOn == startupRegistration.IsEnabled,
            noActivityPage = Navigation.MenuItems.Count == 2,
            noSuccessNotice = !Notice.IsOpen,
            taskDirections = bindings.All(row =>
                row.Action.Items.Cast<ComboBoxItem>().Count(item => (GestureAction)item.Tag == GestureAction.PreviousTask && (string)item.Content == "上一个任务") == 1 &&
                row.Action.Items.Cast<ComboBoxItem>().Count(item => (GestureAction)item.Tag == GestureAction.SwitchApp && (string)item.Content == "下一个任务") == 1 &&
                row.Action.Items.Cast<ComboBoxItem>().All(item => (GestureAction)item.Tag != GestureAction.AppSwitcher)),
            continuousActionsAvailable = bindings.All(row =>
                row.Action.Items.Cast<ComboBoxItem>().Any(item => (GestureAction)item.Tag == GestureAction.ScrollUp) &&
                row.Action.Items.Cast<ComboBoxItem>().Any(item => (GestureAction)item.Tag == GestureAction.BrightnessUp)),
            typingGuardAvailable = typingGuardToggle.Parent is not null && typingGuardToggle.IsEnabled && typingGuardToggle.ActualWidth > 0,
            minimizeToTrayAvailable = minimizeToTray.Parent is not null && minimizeToTray.IsEnabled && minimizeToTray.ActualWidth > 0,
            selectionStepAvailable = selectionDistance.Parent is not null && selectionDistance.IsEnabled && selectionDistance.ActualWidth > 0
        };
        workspace.ClearSelection();
        return state;
    }
    private object ExerciseSpatialEditor()
    {
        workspace!.UpdateLayout();
        var centered = workspace.InspectLayout();
        var positions = new List<object>();
        var narrowPositions = new List<object>();
        bool selectionKeepsClean = SaveButton.Visibility == Visibility.Collapsed;
        foreach (var region in Enum.GetValues<PadRegion>())
        {
            preview.SelectRegion(region);
            positions.Add(workspace.InspectLayout());
            selectionKeepsClean &= SaveButton.Visibility == Visibility.Collapsed;
        }
        preview.SelectRegion(PadRegion.Left);
        var row = bindings.Single(b => b.Id == GestureId.LeftInward);
        int previous = row.Action.SelectedIndex;
        string previousShortcut = row.Shortcut.Text;
        row.Action.SelectedIndex = (int)GestureAction.Shortcut;
        row.Shortcut.Text = "Ctrl+Shift+T";
        bool shortcutVisible = row.Shortcut.Visibility == Visibility.Visible;
        preview.SelectRegion(PadRegion.Top);
        preview.SelectRegion(PadRegion.Left);
        bool draftSurvivesSwitch = row.Shortcut.Text == "Ctrl+Shift+T" && row.Action.SelectedIndex == (int)GestureAction.Shortcut;
        workspace.Width = 440;
        workspace.Height = 330;
        foreach (var region in Enum.GetValues<PadRegion>())
        {
            preview.SelectRegion(region);
            narrowPositions.Add(workspace.InspectLayout());
        }
        workspace.Width = double.NaN;
        workspace.Height = double.NaN;
        workspace.InspectLayout();
        workspace.SetResizeMode(true);
        bool resizeReturnsCenter = preview.IsResizing && preview.SelectedRegion is null;
        workspace.SetResizeMode(false);
        loadingSettings = true;
        row.Action.SelectedIndex = previous;
        row.Shortcut.Text = previousShortcut;
        SyncDraftToPreview();
        loadingSettings = false;
        SaveButton.Visibility = Visibility.Collapsed;
        return new { centered, positions, narrowPositions, selectionKeepsClean, shortcutVisible, draftSurvivesSwitch, resizeReturnsCenter };
    }
    private object ExerciseSettingsEditor()
    {
        workspace!.ShowSettings();
        var normal = workspace.InspectLayout();
        bool openingKeepsClean = SaveButton.Visibility == Visibility.Collapsed;
        workspace.DismissSelectionFrom(distance);
        bool inputKeepsOpen = workspace.IsSettingsOpen;
        workspace.DismissSelectionFrom(distance.Parent);
        bool cardKeepsOpen = workspace.IsSettingsOpen;
        double previousDistance = distance.Value;
        distance.Value = previousDistance < 25 ? previousDistance + .5 : previousDistance - .5;
        bool parameterMarksDirty = SaveButton.Visibility == Visibility.Visible && SaveButton.IsEnabled;
        double editedDistance = distance.Value;
        workspace.Width = 440; workspace.Height = 330;
        var narrow = workspace.InspectLayout();
        workspace.Width = double.NaN; workspace.Height = double.NaN;
        workspace.InspectLayout();
        workspace.DismissSelectionFrom(preview);
        bool blankClosesSettings = !workspace.IsSettingsOpen;
        var afterBlank = workspace.InspectLayout();
        workspace.ShowSettings();
        bool draftSurvivesDismiss = distance.Value == editedDistance;
        preview.SelectRegion(PadRegion.Left);
        workspace.InspectLayout();
        workspace.DismissSelectionFrom(bindings.Single(b => b.Id == GestureId.LeftInward).Action);
        bool actionKeepsOpen = preview.SelectedRegion == PadRegion.Left;
        workspace.DismissSelectionFrom(preview);
        bool blankClearsRegion = preview.SelectedRegion is null && !workspace.IsSettingsOpen;
        workspace.ShowSettings();
        preview.SelectRegion(null);
        bool centerClosesSettings = !workspace.IsSettingsOpen;
        loadingSettings = true;
        distance.Value = previousDistance;
        loadingSettings = false;
        SaveButton.Visibility = Visibility.Collapsed;
        return new { normal, narrow, afterBlank, openingKeepsClean, inputKeepsOpen, cardKeepsOpen,
            parameterMarksDirty, blankClosesSettings, draftSurvivesDismiss,
            actionKeepsOpen, blankClearsRegion, centerClosesSettings };
    }
    // Runs only with --smoke. Uses the same editor event path and persistence as an actual save,
    // with a temporary file; the user's settings file is never touched.
    private object ExerciseEditor()
    {
        bool initiallyHidden = SaveButton.Visibility == Visibility.Collapsed;
        double oldWidth = preview.Ranges.Top;
        double newWidth = oldWidth > .15 ? .10 : oldWidth + .01;
        preview.ChangeWidth(PadEdge.Top, newWidth);
        bool dirty = SaveButton.Visibility == Visibility.Visible && SaveButton.IsEnabled;
        preview.Update(null, runtime!.Settings);
        bool draftSurvivesRefresh = preview.Ranges.Top == newWidth;
        Navigation.SelectedItem = Navigation.MenuItems[1];
        bool visibleOnOtherPage = SaveButton.Visibility == Visibility.Visible && SaveButton.IsEnabled;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TouchPadAdvanced-ui-" + Guid.NewGuid().ToString("N") + ".json");
        bool saved, retained, restored;
        try
        {
            saved = ApplySettings(path);
            retained = SaveButton.Visibility == Visibility.Visible && !SaveButton.IsEnabled && (string)SaveButton.Content == "已保存";
            var persisted = saved ? GestureSettingsStore.Load(path) : null;
            restored = saved && persisted!.EdgeRanges == preview.Ranges && persisted.SelectionDistance == selectionDistance.Value / 100;
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        preview.ChangeWidth(PadEdge.Top, oldWidth);
        bool dirtyAgain = SaveButton.Visibility == Visibility.Visible && SaveButton.IsEnabled;
        return new { initiallyHidden, dirty, draftSurvivesRefresh, visibleOnOtherPage, saved, retained, restored, dirtyAgain };
    }
    // Test messages stay on the app's focused HWND. No global keyboard input is injected.
    internal async Task<object> ExerciseShortcutInputAsync()
    {
        var row = bindings.Single(b => b.Id == GestureId.LeftInward);
        object previousAction = row.Action.SelectedItem;
        string previousText = row.Shortcut.Text;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "TouchPadAdvanced-input-" + Guid.NewGuid().ToString("N") + ".json");
        preview.SelectRegion(PadRegion.Left);
        row.Action.SelectedItem = row.Action.Items.Cast<ComboBoxItem>()
            .Single(item => (GestureAction)item.Tag == GestureAction.Shortcut);
        ((FrameworkElement)Content).UpdateLayout();
        await Task.Delay(80);
        bool automaticallyFocused = ReferenceEquals(FocusManager.GetFocusedElement(((FrameworkElement)Content).XamlRoot), row.Shortcut);
        workspace!.DismissSelectionFrom(row.Action.SelectedItem as DependencyObject);
        bool menuKeepsEditor = preview.SelectedRegion == PadRegion.Left;
        bool canFocus = row.Shortcut.Focus(FocusState.Keyboard);
        ((FrameworkElement)Content).UpdateLayout();
        nint target = ShortcutInputProbe.GetFocus();
        ShortcutInputProbe.GetWindowThreadProcessId(target, out uint owner);
        bool ownsKeyboardTarget = target != 0 && owner == Environment.ProcessId;
        var originalKeys = new byte[256];
        bool stateRead = ShortcutInputProbe.GetKeyboardState(originalKeys);
        bool manualText = false, recordedCombo = false, saved = false, roundTrip = false;
        try
        {
            if (ownsKeyboardTarget && stateRead)
            {
                var typing = (byte[])originalKeys.Clone();
                foreach (int key in new[] {0x10,0x11,0x12,0x5B,0x5C,0xA0,0xA1,0xA2,0xA3,0xA4,0xA5}) typing[key] = 0;
                ShortcutInputProbe.SetKeyboardState(typing);
                row.Shortcut.Text = "";
                foreach (char ch in "Alt+F4")
                    ShortcutInputProbe.PostMessage(target, 0x0102, (nint)ch, 1);
                await Task.Delay(80);
                manualText = row.Shortcut.Text == "Alt+F4";
                typing[0x10] = typing[0x11] = typing[0xA0] = typing[0xA2] = 0x80;
                ShortcutInputProbe.SetKeyboardState(typing);
                uint scan = ShortcutInputProbe.MapVirtualKey(0x54,0);
                ShortcutInputProbe.PostMessage(target,0x0100,0x54,(nint)(1 | scan << 16));
                ShortcutInputProbe.PostMessage(target,0x0101,0x54,(nint)(0xC0000001u | scan << 16));
                await Task.Delay(80);
                recordedCombo = row.Shortcut.Text == "Ctrl+Shift+T";
                saved = ApplySettings(path);
                roundTrip = saved && GestureSettingsStore.Load(path).Binding(row.Id) is
                    { Action: GestureAction.Shortcut, Shortcut: "Ctrl+Shift+T" };
            }
            return new { automaticallyFocused, menuKeepsEditor, canFocus, ownsKeyboardTarget,
                stateRead, manualText, recordedCombo, saved, roundTrip };
        }
        finally
        {
            if (stateRead) ShortcutInputProbe.SetKeyboardState(originalKeys);
            loadingSettings = true;
            row.Action.SelectedItem = previousAction;
            row.Shortcut.Text = previousText;
            SyncDraftToPreview();
            loadingSettings = false;
            SaveButton.Visibility = Visibility.Collapsed;
            workspace.ClearSelection();
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    private static class ShortcutInputProbe
    {
        [DllImport("user32.dll")] internal static extern nint GetFocus();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKeyboardState([Out] byte[] keys);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetKeyboardState(byte[] keys);
        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
        internal static extern uint MapVirtualKey(uint key, uint type);
    }
    internal async Task CaptureEditorAsync(string directory)
    {
        Notice.IsOpen = false;
        foreach (var (region, name) in new (PadRegion?, string)[]
        {
            (null, "center"), (PadRegion.Left, "left"), (PadRegion.Top, "top"),
            (PadRegion.BottomRight, "corner"), (null, "settings")
        })
        {
            if (name == "settings") workspace!.ShowSettings(); else preview.SelectRegion(region);
            ((FrameworkElement)Content).UpdateLayout();
            workspace!.InspectLayout();
            await Task.Delay(260);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(Content);
            var buffer = await bitmap.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var encoded = new DataReader(stream);
            await encoded.LoadAsync((uint)stream.Size);
            var png = new byte[stream.Size];
            encoded.ReadBytes(png);
            await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(directory, $"editor-{name}.png"), png);
        }
    }
}
