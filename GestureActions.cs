using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

[Flags]
public enum ShortcutModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }
public sealed record ShortcutKeys(ShortcutModifiers Modifiers, ushort Key)
{
    internal static ShortcutKeys Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("请填写快捷键，例如 Ctrl+Shift+T。");
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        ShortcutModifiers modifiers = ShortcutModifiers.None;
        ushort? key = null;
        foreach (string part in parts)
        {
            ShortcutModifiers modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ShortcutModifiers.Ctrl,
                "ALT" => ShortcutModifiers.Alt, "SHIFT" => ShortcutModifiers.Shift,
                "WIN" or "WINDOWS" => ShortcutModifiers.Win, _ => ShortcutModifiers.None
            };
            if (modifier != ShortcutModifiers.None)
            {
                if ((modifiers & modifier) != 0) throw new InvalidDataException("快捷键包含重复修饰键。");
                modifiers |= modifier;
            }
            else
            {
                ushort parsed = ParseKey(part);
                if (key.HasValue || parsed == 0)
                    throw new InvalidDataException("shortcut.invalid_key");
                key = parsed;
            }
        }
        return new ShortcutKeys(modifiers, key ?? throw new InvalidDataException("快捷键缺少主按键。"));
    }
    public static string? TryFormatRecorded(ushort key, ShortcutModifiers modifiers)
    {
        const ShortcutModifiers known = ShortcutModifiers.Ctrl | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Win;
        if ((modifiers & ~known) != 0) return null;
        bool function = key is >= 0x70 and <= 0x87;
        // Preserve ordinary typing, Shift+letters and unmodified focus-navigation keys.
        if (!function && (modifiers & ~ShortcutModifiers.Shift) == 0) return null;
        string? name = key switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
            >= 0x70 and <= 0x87 => "F" + (key - 0x70 + 1),
            >= 0x60 and <= 0x69 => "Numpad" + (key - 0x60),
            8 => "Backspace", 9 => "Tab", 13 => "Enter", 27 => "Esc", 32 => "Space",
            33 => "PageUp", 34 => "PageDown", 35 => "End", 36 => "Home",
            37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down",
            44 => "PrintScreen", 45 => "Insert", 46 => "Delete",
            0x6A => "Multiply", 0x6B => "Add", 0x6D => "Subtract",
            0x6E => "Decimal", 0x6F => "Divide",
            0xBA => "Semicolon", 0xBB => "Equal", 0xBC => "Comma",
            0xBD => "Hyphen", 0xBE => "Period", 0xBF => "Slash",
            0xC0 => "Backtick", 0xDB => "LeftBracket", 0xDC => "Backslash",
            0xDD => "RightBracket", 0xDE => "Quote",
            _ => null
        };
        if (name is null) return null;
        var parts = new List<string>();
        if ((modifiers & ShortcutModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ShortcutModifiers.Win) != 0) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
    }
    private static ushort ParseKey(string part)
    {
        string name = part.ToUpperInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return name[0];
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out int function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        if (name.StartsWith("NUMPAD", StringComparison.Ordinal) && name.Length == 7 && char.IsAsciiDigit(name[6]))
            return (ushort)(0x60 + name[6] - '0');
        return name switch
        {
            "BACKSPACE" => 8, "TAB" => 9, "ENTER" or "RETURN" => 13,
            "ESC" or "ESCAPE" => 27, "SPACE" => 32,
            "PAGEUP" or "PRIOR" => 33, "PAGEDOWN" or "NEXT" => 34,
            "END" => 35, "HOME" => 36, "LEFT" => 37, "UP" => 38,
            "RIGHT" => 39, "DOWN" => 40, "PRINTSCREEN" => 44,
            "INSERT" => 45, "DELETE" => 46, "MULTIPLY" => 0x6A,
            "ADD" or "PLUS" => 0x6B, "SUBTRACT" or "MINUS" => 0x6D,
            "DECIMAL" => 0x6E, "DIVIDE" => 0x6F,
            "SEMICOLON" => 0xBA, "EQUAL" => 0xBB, "COMMA" => 0xBC,
            "HYPHEN" => 0xBD, "PERIOD" => 0xBE, "SLASH" => 0xBF,
            "BACKTICK" => 0xC0, "LEFTBRACKET" => 0xDB, "BACKSLASH" => 0xDC,
            "RIGHTBRACKET" => 0xDD, "QUOTE" => 0xDE, _ => 0
        };
    }
}

internal static class ScreenBrightness
{
    private const uint GenericReadWrite = 0x80000000u | 0x40000000u;
    private const uint FileShareReadWrite = 1u | 2u;
    private const uint OpenExisting = 3u;
    private const uint IoctlQuerySupported = 0x00230494u;
    private const uint IoctlQueryBrightness = 0x00230498u;
    private const uint IoctlSetBrightness = 0x0023049Cu;

    internal static byte ComputeNextLevel(byte current, int deltaPercent, ReadOnlySpan<byte> supported)
    {
        int target = Math.Clamp(current + deltaPercent, 0, 100);
        if (supported.IsEmpty) return (byte)target;
        byte best = supported[0];
        int bestDiff = Math.Abs(best - target);
        for (int i = 1; i < supported.Length; i++)
        {
            int diff = Math.Abs(supported[i] - target);
            if (diff < bestDiff || (diff == bestDiff && (deltaPercent >= 0 ? supported[i] > best : supported[i] < best)))
            {
                best = supported[i];
                bestDiff = diff;
            }
        }
        return best;
    }

    internal static void Adjust(int steps)
    {
        nint handle = Native.CreateFile(@"\\.\LCD", GenericReadWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle == 0 || handle == -1)
            throw new InvalidOperationException("当前显示器不支持内置面板亮度调节。");
        try
        {
            byte[] state = new byte[3];
            if (!Native.DeviceIoControl(handle, IoctlQueryBrightness, null, 0, state, (uint)state.Length, out uint read, 0) || read < 3)
                throw new InvalidOperationException("无法读取当前屏幕亮度。");
            byte[] supportedBuffer = new byte[256];
            ReadOnlySpan<byte> supported = Native.DeviceIoControl(handle, IoctlQuerySupported, null, 0,
                supportedBuffer, (uint)supportedBuffer.Length, out uint supCount, 0) && supCount > 0
                ? supportedBuffer.AsSpan(0, (int)supCount) : ReadOnlySpan<byte>.Empty;
            byte current = state[0] == 1 ? state[1] : state[2];
            byte next = ComputeNextLevel(current, steps * 5, supported);
            byte[] update = [3, next, next];
            if (!Native.DeviceIoControl(handle, IoctlSetBrightness, update, (uint)update.Length, null, 0, out _, 0))
                throw new InvalidOperationException("无法设置屏幕亮度。");
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}

public static class GestureActions
{
    internal const nuint InputTag = 0x54504145;
    internal const uint MouseWheelFlag = 0x0800;
    internal const uint MouseHWheelFlag = 0x1000;
    private const int WheelDelta = 120;
    private static readonly (ShortcutModifiers Modifier, ushort Key)[] Modifiers =
    [
        (ShortcutModifiers.Ctrl, 0x11), (ShortcutModifiers.Alt, 0x12),
        (ShortcutModifiers.Shift, 0x10), (ShortcutModifiers.Win, 0x5B)
    ];

    internal static Native.Input[] Build(GestureBinding binding, int steps = 1,
        ShortcutModifiers alreadyHeld = ShortcutModifiers.None)
    {
        if (steps is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(steps));
        if (binding.Action is GestureAction.None or GestureAction.BrightnessUp or GestureAction.BrightnessDown) return [];
        if (binding.Action is GestureAction.Back or GestureAction.Forward)
        {
            uint button = binding.Action == GestureAction.Back ? 1u : 2u;
            return [Mouse(button, 0x80), Mouse(button, 0x100)];
        }
        if (binding.Action is GestureAction.ScrollUp or GestureAction.ScrollDown or GestureAction.ScrollLeft or GestureAction.ScrollRight)
        {
            uint flags = binding.Action is GestureAction.ScrollUp or GestureAction.ScrollDown ? MouseWheelFlag : MouseHWheelFlag;
            int delta = binding.Action is GestureAction.ScrollUp or GestureAction.ScrollRight ? WheelDelta : -WheelDelta;
            return Enumerable.Range(0, steps).Select(_ => Mouse(unchecked((uint)delta), flags)).ToArray();
        }
        var shortcutAction = BuiltInShortcut(binding.Action);
        if (binding.Action == GestureAction.Shortcut || shortcutAction is not null)
        {
            var shortcut = ShortcutKeys.Parse(shortcutAction ?? binding.Shortcut);
            var result = new List<Native.Input>();
            ushort[] modifiers = Modifiers.Where(m => (shortcut.Modifiers & m.Modifier) != 0 &&
                (alreadyHeld & m.Modifier) == 0).Select(m => m.Key).ToArray();
            foreach (ushort modifier in modifiers) result.Add(Key(modifier, false));
            result.Add(Key(shortcut.Key, false));
            result.Add(Key(shortcut.Key, true));
            foreach (ushort modifier in modifiers.Reverse()) result.Add(Key(modifier, true));
            return result.ToArray();
        }
        ushort key = binding.Action switch
        {
            GestureAction.Mute => 0xAD, GestureAction.VolumeDown => 0xAE,
            GestureAction.VolumeUp => 0xAF, GestureAction.NextTrack => 0xB0,
            GestureAction.PreviousTrack => 0xB1, GestureAction.PlayPause => 0xB3,
            _ => throw new ArgumentOutOfRangeException(nameof(binding))
        };
        int repeat = GestureCatalog.Repeats(binding.Action) ? steps : 1;
        return Enumerable.Range(0, repeat).SelectMany(_ => new[] { Key(key, false), Key(key, true) }).ToArray();
    }

    internal static void Execute(GestureInvocation invocation)
    {
        if (invocation.Binding.Action is GestureAction.BrightnessUp or GestureAction.BrightnessDown)
        {
            int signedSteps = invocation.Binding.Action == GestureAction.BrightnessUp ? invocation.Steps : -invocation.Steps;
            ScreenBrightness.Adjust(signedSteps);
            return;
        }
        ShortcutModifiers held = ShortcutModifiers.None;
        var shortcutAction = BuiltInShortcut(invocation.Binding.Action);
        if (invocation.Binding.Action == GestureAction.Shortcut || shortcutAction is not null)
        {
            var shortcut = ShortcutKeys.Parse(shortcutAction ?? invocation.Binding.Shortcut);
            foreach (var modifier in Modifiers)
                if (IsHeld(modifier.Key)) held |= modifier.Modifier;
            if (IsHeld(0x5C)) held |= ShortcutModifiers.Win;
            if ((held & ~shortcut.Modifiers) != 0 || IsHeld(shortcut.Key))
                throw new InvalidOperationException("当前有其他快捷键按键被按住；本次动作未发送。");
        }
        var inputs = Build(invocation.Binding, invocation.Steps, held);
        if (inputs.Length == 0) return;
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
        if (sent == inputs.Length) return;

        int error = Marshal.GetLastWin32Error();
        // Release only events actually injected in the successful prefix.
        var releases = PendingReleases(inputs.Take((int)sent));
        if (releases.Length != 0)
            Native.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Native.Input>());
        throw new InvalidOperationException($"动作发送失败：已发送 {sent}/{inputs.Length} 个输入事件，错误码 {error}。");
    }

    internal static Native.Input[] PendingReleases(IEnumerable<Native.Input> sent)
    {
        var held = new List<Native.Input>();
        foreach (var input in sent)
        {
            if (input.Type == 0 && (input.Mouse.Flags & (0x80 | 0x100)) == 0) continue;
            bool up = input.Type == 1 ? (input.Keyboard.Flags & 2) != 0 : (input.Mouse.Flags & 0x100) != 0;
            if (!up) held.Add(input);
            else
            {
                int index = held.FindLastIndex(d => d.Type == input.Type &&
                    (d.Type == 1 ? d.Keyboard.VirtualKey == input.Keyboard.VirtualKey
                        : d.Mouse.Data == input.Mouse.Data));
                if (index >= 0) held.RemoveAt(index);
            }
        }
        return held.AsEnumerable().Reverse().Select(d => d.Type == 1
            ? Key(d.Keyboard.VirtualKey, true) : Mouse(d.Mouse.Data, 0x100)).ToArray();
    }

    internal static string? BuiltInShortcut(GestureAction action) => action switch
    {
        GestureAction.SwitchApp => "Alt+Tab",
        GestureAction.PreviousTask => "Alt+Shift+Tab",
        GestureAction.AppSwitcher => "Ctrl+Alt+Tab",
        GestureAction.TaskView => "Win+Tab",
        GestureAction.StartMenu => "Ctrl+Esc",
        GestureAction.ShowDesktop => "Win+D",
        GestureAction.PreviousDesktop => "Win+Ctrl+Left",
        GestureAction.NextDesktop => "Win+Ctrl+Right",
        GestureAction.QuickSettings => "Win+A",
        GestureAction.Notifications => "Win+N",
        GestureAction.FileExplorer => "Win+E",
        _ => null
    };
    private static bool IsHeld(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private static Native.Input Mouse(uint button, uint flags) => new()
    {
        Mouse = new Native.MouseInput { Data = button, Flags = flags, ExtraInfo = InputTag }
    };

    internal static Native.Input Key(ushort key, bool up) => new()
    {
        Type = 1, Keyboard = new Native.KeyboardInput
        {
            VirtualKey = key, Flags = (up ? 2u : 0u) | (key is >= 0x21 and <= 0x2E or 0x5B or 0x5C ? 1u : 0u),
            ExtraInfo = InputTag
        }
    };
}
