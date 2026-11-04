using System.Text.Json;
using System.Text.Json.Serialization;

namespace TouchPadAdvanced;

public enum PadEdge { Top, Bottom, Left, Right }
public enum PadCorner { TopLeft, TopRight, BottomLeft, BottomRight }
public enum GestureId
{
    TopSlideLeft, TopSlideRight, BottomSlideLeft, BottomSlideRight,
    LeftSlideUp, LeftSlideDown, RightSlideUp, RightSlideDown,
    TopLeftInward, TopRightInward, BottomLeftInward, BottomRightInward,
    TopInward, BottomInward, LeftInward, RightInward
}
public enum GestureAction
{
    None, Back, Forward, VolumeUp, VolumeDown, Mute, PlayPause, PreviousTrack, NextTrack, Shortcut,
    SwitchApp, TaskView, StartMenu, ShowDesktop, PreviousDesktop, NextDesktop,
    QuickSettings, Notifications, FileExplorer, AppSwitcher, PreviousTask,
    ScrollUp, ScrollDown, ScrollLeft, ScrollRight, BrightnessUp, BrightnessDown
}

public sealed record GestureSpec(GestureId Id, PadEdge? Edge = null,
    bool Positive = false, PadCorner? Corner = null, bool Inward = false);

public static class GestureCatalog
{
    public static readonly GestureSpec[] All =
    [
        new(GestureId.TopSlideLeft, PadEdge.Top),
        new(GestureId.TopSlideRight, PadEdge.Top, true),
        new(GestureId.BottomSlideLeft, PadEdge.Bottom),
        new(GestureId.BottomSlideRight, PadEdge.Bottom, true),
        new(GestureId.LeftSlideUp, PadEdge.Left),
        new(GestureId.LeftSlideDown, PadEdge.Left, true),
        new(GestureId.RightSlideUp, PadEdge.Right),
        new(GestureId.RightSlideDown, PadEdge.Right, true),
        new(GestureId.TopLeftInward, Corner: PadCorner.TopLeft),
        new(GestureId.TopRightInward, Corner: PadCorner.TopRight),
        new(GestureId.BottomLeftInward, Corner: PadCorner.BottomLeft),
        new(GestureId.BottomRightInward, Corner: PadCorner.BottomRight),
        new(GestureId.TopInward, PadEdge.Top, Inward: true),
        new(GestureId.BottomInward, PadEdge.Bottom, Inward: true),
        new(GestureId.LeftInward, PadEdge.Left, Inward: true),
        new(GestureId.RightInward, PadEdge.Right, Inward: true)
    ];

    public static GestureSpec Get(GestureId id) => All.Single(s => s.Id == id);

    public static bool SelectsTask(GestureAction action) =>
        action is GestureAction.SwitchApp or GestureAction.PreviousTask or GestureAction.AppSwitcher;

    public static bool Selects(GestureAction action) =>
        SelectsTask(action) || action is GestureAction.PreviousDesktop or GestureAction.NextDesktop;

    public static int SelectionDirection(GestureAction action) => action switch
    {
        GestureAction.PreviousTask or GestureAction.PreviousDesktop => -1,
        GestureAction.SwitchApp or GestureAction.AppSwitcher or GestureAction.NextDesktop => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public static bool Repeats(GestureAction action) =>
        action is GestureAction.VolumeUp or GestureAction.VolumeDown
            or GestureAction.ScrollUp or GestureAction.ScrollDown
            or GestureAction.ScrollLeft or GestureAction.ScrollRight
            or GestureAction.BrightnessUp or GestureAction.BrightnessDown;
}

public sealed record GestureBinding(GestureId Gesture, GestureAction Action, string Shortcut = "");

public sealed record EdgeRecognitionRanges(double Top = 0.08, double Bottom = 0.08,
    double Left = 0.08, double Right = 0.08)
{
    public const double Minimum = 0.02, Maximum = 0.20;

    public EdgeRecognitionRanges WithWidth(PadEdge edge, double width)
    {
        if (!double.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Clamp(width, Minimum, Maximum);
        return edge switch
        {
            PadEdge.Top => this with { Top = width }, PadEdge.Bottom => this with { Bottom = width },
            PadEdge.Left => this with { Left = width }, PadEdge.Right => this with { Right = width },
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    public double Width(PadEdge edge) => edge switch
    {
        PadEdge.Top => Top, PadEdge.Bottom => Bottom, PadEdge.Left => Left, PadEdge.Right => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };
}
public sealed record GestureSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool ObserveOnly { get; init; }
    public bool CapturePointer { get; init; } = true;
    public bool SuppressWhileTyping { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
    public double EdgeWidth { get; init; } = 0.08; // Legacy unified-width configuration.
    public EdgeRecognitionRanges? EdgeRanges { get; init; }
    public double GetEdgeWidth(PadEdge edge) => EdgeRanges?.Width(edge) ?? EdgeWidth;
    public double TriggerDistance { get; init; } = 0.06;
    public double RepeatDistance { get; init; } = 0.04;
    public double SelectionDistance { get; init; } = 0.12;
    public GestureBinding[] Bindings { get; init; } = GestureCatalog.All.Select(s =>
        new GestureBinding(s.Id, s.Id switch
        {
            GestureId.BottomSlideLeft => GestureAction.Back,
            GestureId.BottomSlideRight => GestureAction.Forward,
            GestureId.RightSlideUp => GestureAction.VolumeUp,
            GestureId.RightSlideDown => GestureAction.VolumeDown,
            _ => GestureAction.None
        })).ToArray();

    public GestureBinding Binding(GestureId id) => Bindings.Single(b => b.Gesture == id);

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的边缘配置版本。");
        if (!double.IsFinite(EdgeWidth) || EdgeWidth is < 0.02 or > 0.20)
            throw new InvalidDataException("边缘区域宽度须为 2%–20%。");
        if (!double.IsFinite(TriggerDistance) || TriggerDistance is < 0.01 or > 0.30)
            throw new InvalidDataException("触发距离须为 1%–30%。");
        if (!double.IsFinite(RepeatDistance) || RepeatDistance is < 0.01 or > 0.20)
            throw new InvalidDataException("连续调节步长须为 1%–20%。");
        if (Enum.GetValues<PadEdge>().Any(edge => !double.IsFinite(GetEdgeWidth(edge)) || GetEdgeWidth(edge) is < 0.02 or > 0.20))
            throw new InvalidDataException("每条边的识别范围须为 2%–20%。");
        if (!double.IsFinite(SelectionDistance) || SelectionDistance is < 0.02 or > 0.30)
            throw new InvalidDataException("切换选择步长须为 2%–30%。");
        if (Bindings is null || Bindings.Length != GestureCatalog.All.Length ||
            Bindings.Any(b => b is null || b.Shortcut is null || !Enum.IsDefined(b.Gesture) || !Enum.IsDefined(b.Action)) ||
            Bindings.Select(b => b.Gesture).Distinct().Count() != GestureCatalog.All.Length)
            throw new InvalidDataException("配置必须包含 16 个不重复的触发项。");
        foreach (var binding in Bindings.Where(b => b.Action == GestureAction.Shortcut))
            _ = ShortcutKeys.Parse(binding.Shortcut);
    }
}

public static class GestureSettingsStore
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TouchPadAdvanced", "edge-settings.json");

    internal static readonly JsonSerializerOptions Options = new(Diagnostics.JsonOptions)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static GestureSettings Load(string? path = null)
    {
        path ??= Path;
        if (!File.Exists(path)) return new GestureSettings();
        var settings = JsonSerializer.Deserialize<GestureSettings>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("边缘配置为空。");
        // Only the complete original catalog is a legacy configuration. Never repair partial/corrupt files.
        if (settings.SchemaVersion == 1 && settings.Bindings is { Length: 12 } old &&
            old.All(b => b is not null && (int)b.Gesture is >= 0 and < 12) &&
            old.Select(b => b.Gesture).Distinct().Count() == 12)
            settings = settings with
            {
                Bindings = old.Concat(GestureCatalog.All.Skip(12)
                    .Select(s => new GestureBinding(s.Id, GestureAction.None))).ToArray()
            };
        settings.Validate();
        return settings;
    }

    public static void Save(GestureSettings settings, string? path = null)
    {
        settings.Validate();
        path ??= Path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
