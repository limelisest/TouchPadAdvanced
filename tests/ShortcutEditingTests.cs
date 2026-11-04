namespace TouchPadAdvanced;

internal static class ShortcutEditingTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("Recorded shortcuts normalize modifiers and match actual virtual keys", Recording),
        ("Shortcut recording preserves normal typing and focus navigation", Editing),
        ("Recorded shortcut bindings survive validation and persistence", Persistence)
    ];
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }
    private static void Recording()
    {
        foreach(var (key,mods,text) in new (ushort,ShortcutModifiers,string)[]
        {
            (0x54,ShortcutModifiers.Ctrl|ShortcutModifiers.Shift,"Ctrl+Shift+T"),
            (0x73,ShortcutModifiers.Alt,"Alt+F4"),
            (0x53,ShortcutModifiers.Win|ShortcutModifiers.Shift,"Shift+Win+S"),
            (0x74,ShortcutModifiers.None,"F5"),
            (0x87,ShortcutModifiers.Shift,"Shift+F24"),
            (0x25,ShortcutModifiers.Ctrl|ShortcutModifiers.Alt,"Ctrl+Alt+Left"),
            (0xBD,ShortcutModifiers.Ctrl,"Ctrl+Hyphen"),
            (0x60,ShortcutModifiers.Ctrl,"Ctrl+Numpad0")
        })
        {
            Check(ShortcutKeys.TryFormatRecorded(key,mods)==text,"Canonical recording: "+text);
            Check(ShortcutKeys.Parse(text)==new ShortcutKeys(mods,key),"Recording must parse to the same key.");
        }
        for(ushort key=0;key<256;key++)
        {
            var text=ShortcutKeys.TryFormatRecorded(key,ShortcutModifiers.Ctrl|ShortcutModifiers.Alt);
            if(text is not null)
                Check(ShortcutKeys.Parse(text)==new ShortcutKeys(ShortcutModifiers.Ctrl|ShortcutModifiers.Alt,key),
                    "Every recordable key must be understood by execution.");
        }
    }
    private static void Editing()
    {
        foreach(ushort key in new ushort[]{0x41,0x39,0x20,0x09,0x0D,0x1B,0x25,0x2E})
            Check(ShortcutKeys.TryFormatRecorded(key,ShortcutModifiers.None) is null,
                "Unmodified typing and editor navigation must reach the text control.");
        Check(ShortcutKeys.TryFormatRecorded(0x41,ShortcutModifiers.Shift) is null,"Shift+letter remains ordinary typing.");
        foreach(ushort key in new ushort[]{0x10,0x11,0x12,0x5B,0x5C,0xA0,0xA2,0xA4,0xFF})
            Check(ShortcutKeys.TryFormatRecorded(key,ShortcutModifiers.Ctrl) is null,
                "A modifier or unsupported key cannot replace an existing shortcut.");
    }
    private static void Persistence()
    {
        string path=Path.Combine(Path.GetTempPath(),"TouchPadAdvanced-shortcut-"+Guid.NewGuid().ToString("N")+".json");
        string text=ShortcutKeys.TryFormatRecorded(0xBB,ShortcutModifiers.Ctrl|ShortcutModifiers.Shift)!;
        var settings=new GestureSettings
        {
            Bindings=new GestureSettings().Bindings.Select(b=>b.Gesture==GestureId.TopInward
                ?b with{Action=GestureAction.Shortcut,Shortcut=text}:b).ToArray()
        };
        try
        {
            GestureSettingsStore.Save(settings,path);
            var saved=GestureSettingsStore.Load(path).Binding(GestureId.TopInward);
            Check(saved.Action==GestureAction.Shortcut && saved.Shortcut==text &&
                ShortcutKeys.Parse(saved.Shortcut)==new ShortcutKeys(ShortcutModifiers.Ctrl|ShortcutModifiers.Shift,0xBB),
                "Recorded punctuation and modifiers survive save and load.");
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
}
