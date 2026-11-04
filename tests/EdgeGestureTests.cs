using System.Runtime.InteropServices;
using System.Text.Json;

namespace TouchPadAdvanced;

internal static class EdgeGestureTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("All four edge-inward directions dispatch independently", EdgeInward),
        ("Edge-inward tracking survives leaving the band and captures until release", InwardTracking),
        ("Edge-inward motion rejects outward and ambiguous diagonals", InwardDirection),
        ("Corner intersections retain their own gesture ownership", InwardCorners),
        ("Edge-inward volume stays discrete and invalid contacts release capture", InwardLifecycle),
        ("Legacy twelve bindings migrate preserving settings and actions", LegacyBindings),
        ("Partial and duplicated legacy catalogs are not silently repaired", InvalidLegacyBindings),
        ("Preview range resizing clamps each edge independently", RangeResizing),
        ("All eight edge directions dispatch the configured action", AllEdges),
        ("All four corners dispatch inward motion once", AllCorners),
        ("Corner diagonal and adjacent tangent gestures remain distinct", CornerArbitration),
        ("Pointer contact enters an edge without lifting and resets its swipe origin", CenterEntry),
        ("Subthreshold movement and stationary contact never fire", Threshold),
        ("Navigation fires once across reversal in a contact cycle", NavigationOnce),
        ("Volume accumulates by displacement and can reverse", Volume),
        ("Volume results are independent of packet segmentation", Segmentation),
        ("Multiple fingers, buttons and low confidence cancel the whole contact cycle", InvalidContacts),
        ("Contact replacement and interruption require a clean release", Discontinuity),
        ("Leaving an untriggered edge restores pointer motion and allows fresh entry", Corridor),
        ("Starting or resuming during a contact waits for release", InitialRelease),
        ("Corner volume remains a discrete action", CornerVolume),
        ("Configuration round trips and persists without temporary residue", Configuration),
        ("Invalid configurations and ambiguous shortcut strings are rejected", Validation),
        ("Native input layouts and true side button encoding", InputEncoding),
        ("Shortcut modifiers, held keys and extended navigation encoding", Shortcuts),
        ("Partial injection cleanup releases only the injected prefix", Cleanup),
        ("Continuous scroll and brightness actions step, reverse and encode cleanly", ContinuousScrollAndBrightness),
        ("Typing guard suppresses palm touchdown during cooldown and tracks origin", TypingGuardAndOrigin)
    ];

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid data to be rejected.");
    }

    private static Contact C(double x, double y, int id = 0, bool? confidence = true) =>
        new(id, (int)Math.Round(x * 10000), (int)Math.Round(y * 10000), true,
            confidence, null, null, null, new Axis(0, 10000), new Axis(0, 10000));

    private static TouchFrame F(params Contact[] contacts) => new(DateTimeOffset.UnixEpoch, 0, 0, contacts);
    private static EdgeGestureEngine Engine(GestureSettings? settings = null) => new(settings ?? All(), false);
    private static GestureSettings All() => new()
    {
        Bindings = GestureCatalog.All.Select(s => new GestureBinding(s.Id, GestureAction.Back)).ToArray()
    };
    private static void Clear(EdgeGestureEngine engine) => engine.Process(F());

    private static readonly (GestureId Id, double X, double Y, double Dx, double Dy)[] InwardCases =
    [
        (GestureId.TopInward,.5,.03,0,.3), (GestureId.BottomInward,.5,.97,0,-.3),
        (GestureId.LeftInward,.03,.5,.3,0), (GestureId.RightInward,.97,.5,-.3,0)
    ];

    private static void EdgeInward()
    {
        foreach (var c in InwardCases)
        {
            var engine = Engine();
            Check(engine.Process(F(C(c.X,c.Y))) is null, "Touchdown does not trigger.");
            Check(engine.Process(F(C(c.X+c.Dx,c.Y+c.Dy)))?.Binding.Gesture == c.Id, c.Id.ToString());
            Check(engine.Process(F(C(.5,.5))) is null, "One action until release.");
        }
    }

    private static void InwardTracking()
    {
        foreach (var c in InwardCases)
        {
            var engine = Engine(All() with { TriggerDistance = .2 });
            engine.Process(F(C(c.X,c.Y)));
            Check(engine.OwnsPointer, "Claim at origin.");
            Check(engine.Process(F(C(c.X+c.Dx/2,c.Y+c.Dy/2))) is null && engine.OwnsPointer,
                "Leaving the band below threshold must retain the inward candidate.");
            Check(engine.Process(F(C(c.X+c.Dx,c.Y+c.Dy)))?.Binding.Gesture == c.Id && engine.OwnsPointer,
                "Trigger outside the band and retain ownership.");
            Clear(engine);
            Check(!engine.OwnsPointer, "Lift releases capture.");
        }
    }

    private static void InwardDirection()
    {
        foreach (var c in InwardCases)
        {
            var settings = All() with
            {
                TriggerDistance = .01,
                Bindings = All().Bindings.Select(b => b with
                    { Action = b.Gesture == c.Id ? GestureAction.Back : GestureAction.None }).ToArray()
            };
            var engine = Engine(settings);
            engine.Process(F(C(c.X,c.Y)));
            Check(engine.Process(F(C(c.X-c.Dx/15,c.Y-c.Dy/15))) is null, "Outward movement.");
            Clear(engine);
            engine.Process(F(C(c.X,c.Y)));
            double dx = c.Dx == 0 ? .1 : c.Dx/3, dy = c.Dy == 0 ? .1 : c.Dy/3;
            Check(engine.Process(F(C(c.X+dx,c.Y+dy))) is null, "Ambiguous diagonal.");
        }
    }

    private static void InwardCorners()
    {
        var settings = All() with
        {
            Bindings = All().Bindings.Select(b => b with
                { Action = GestureCatalog.Get(b.Gesture).Inward ? GestureAction.Back : GestureAction.None }).ToArray()
        };
        foreach (var (x,y) in new[] {(.03,.03),(.97,.03),(.03,.97),(.97,.97)})
        {
            var engine = Engine(settings);
            engine.Process(F(C(x,y)));
            Check(!engine.OwnsPointer && engine.Process(F(C(.5,.5))) is null,
                "An unbound corner does not inherit its neighboring perpendicular swipes.");
        }
        CornerArbitration();
    }

    private static void InwardLifecycle()
    {
        foreach (var c in InwardCases)
        {
            var settings = All() with
            {
                Bindings = All().Bindings.Select(b => b with { Action = GestureAction.VolumeUp }).ToArray()
            };
            var engine = Engine(settings);
            engine.Process(F(C(c.X,c.Y)));
            Check(engine.Process(F(C(c.X+c.Dx,c.Y+c.Dy))) is { Steps: 1 }, "Inward volume is discrete.");
            Check(engine.Process(F(C(.5,.5))) is null && engine.OwnsPointer, "No repeats.");
            Clear(engine);
            engine.Process(F(C(c.X,c.Y)));
            engine.Process(F(C(c.X,c.Y),C(.5,.5,1)));
            Check(!engine.OwnsPointer && engine.Process(F(C(c.X+c.Dx,c.Y+c.Dy))) is null,
                "Second finger cancels and releases.");
        }
    }

    private static void LegacyBindings()
    {
        string path = Path.Combine(Path.GetTempPath(),"TouchPadAdvanced-legacy-"+Guid.NewGuid().ToString("N")+".json");
        var old = All() with { Enabled = false, CapturePointer = false, ObserveOnly = true, EdgeWidth = .12,
            EdgeRanges = new(.03,.13,.05,.17), Bindings = All().Bindings.Take(12).ToArray() };
        try
        {
            File.WriteAllText(path,JsonSerializer.Serialize(old,GestureSettingsStore.Options));
            var restored = GestureSettingsStore.Load(path);
            Check(!restored.Enabled && !restored.CapturePointer && restored.ObserveOnly &&
                restored.EdgeWidth == old.EdgeWidth && restored.EdgeRanges == old.EdgeRanges, "Preserve settings.");
            Check(restored.Bindings.Take(12).SequenceEqual(old.Bindings), "Preserve original actions.");
            Check(restored.Bindings.Length == 16 && restored.Bindings.Skip(12).All(b => b.Action == GestureAction.None),
                "Add only four unbound gestures.");
            GestureSettingsStore.Save(restored,path);
            Check(GestureSettingsStore.Load(path).Bindings.SequenceEqual(restored.Bindings), "Migrated round trip.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void InvalidLegacyBindings()
    {
        string path = Path.Combine(Path.GetTempPath(),"TouchPadAdvanced-invalid-"+Guid.NewGuid().ToString("N")+".json");
        try
        {
            foreach (var bindings in new[] { All().Bindings.Take(11).ToArray(),
                Enumerable.Repeat(All().Bindings[0],12).ToArray(),
                All().Bindings.Take(11).Append(All().Bindings[12]).ToArray() })
            {
                File.WriteAllText(path,JsonSerializer.Serialize(All() with { Bindings = bindings },GestureSettingsStore.Options));
                Reject(() => GestureSettingsStore.Load(path));
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RangeResizing()
    {
        var original = new EdgeRecognitionRanges(.03,.13,.05,.17);
        foreach (var edge in Enum.GetValues<PadEdge>())
        {
            var resized = original.WithWidth(edge,.11);
            foreach (var other in Enum.GetValues<PadEdge>())
                Check(resized.Width(other) == (edge == other ? .11 : original.Width(other)), "Resize only selected edge.");
            Check(original.WithWidth(edge,-1).Width(edge) == .02 &&
                original.WithWidth(edge,2).Width(edge) == .20, "Clamp to preview boundaries.");
            (new GestureSettings { EdgeRanges = resized }).Validate();
        }
    }
    private static void AllEdges()
    {
        var cases = new (GestureId, double, double, double, double)[]
        {
            (GestureId.TopSlideLeft,.5,.03,.35,.03),(GestureId.TopSlideRight,.5,.03,.65,.03),
            (GestureId.BottomSlideLeft,.5,.97,.35,.97),(GestureId.BottomSlideRight,.5,.97,.65,.97),
            (GestureId.LeftSlideUp,.03,.5,.03,.35),(GestureId.LeftSlideDown,.03,.5,.03,.65),
            (GestureId.RightSlideUp,.97,.5,.97,.35),(GestureId.RightSlideDown,.97,.5,.97,.65)
        };
        foreach (var (id, x, y, endX, endY) in cases)
        {
            var engine = Engine();
            Check(engine.Process(F(C(x,y))) is null, "Touchdown must not fire.");
            Check(engine.Process(F(C(endX,endY)))?.Binding.Gesture == id, id.ToString());
        }
    }

    private static void AllCorners()
    {
        var cases = new (GestureId,double,double,double,double)[]
        {
            (GestureId.TopLeftInward,.03,.03,.18,.18),(GestureId.TopRightInward,.97,.03,.82,.18),
            (GestureId.BottomLeftInward,.03,.97,.18,.82),(GestureId.BottomRightInward,.97,.97,.82,.82)
        };
        foreach (var (id,x,y,endX,endY) in cases)
        {
            var engine = Engine();
            engine.Process(F(C(x,y)));
            Check(engine.Process(F(C(endX,endY)))?.Binding.Gesture == id, id.ToString());
            Check(engine.Process(F(C(.5,.5))) is null, "Corner action must fire once.");
        }
    }

    private static void CornerArbitration()
    {
        var engine = Engine();
        engine.Process(F(C(.97,.97)));
        Check(engine.Process(F(C(.80,.96)))?.Binding.Gesture == GestureId.BottomSlideLeft, "Bottom tangent.");
        Clear(engine);
        engine.Process(F(C(.97,.97)));
        Check(engine.Process(F(C(.96,.80)))?.Binding.Gesture == GestureId.RightSlideUp, "Right tangent.");
        Clear(engine);
        engine.Process(F(C(.97,.97)));
        Check(engine.Process(F(C(.85,.85)))?.Binding.Gesture == GestureId.BottomRightInward, "Corner diagonal.");
        Clear(engine);
        engine.Process(F(C(.03,.03)));
        Check(engine.Process(F(C(.01,.01))) is null, "Outward motion is not inward.");
    }

    private static void CenterEntry()
    {
        var engine = Engine();
        engine.Process(F(C(.5,.5)));
        Check(!engine.OwnsPointer && engine.Phase == GesturePhase.Pointer, "Center motion is ordinary pointer motion.");
        Check(engine.Process(F(C(.7,.97))) is null && engine.OwnsPointer, "Entering a bound strip claims the pointer without firing.");
        Check(engine.Process(F(C(.69,.97))) is null, "Entry displacement cannot contribute to the threshold.");
        Check(engine.Process(F(C(.6,.97)))?.Binding.Gesture == GestureId.BottomSlideLeft, "Swipe triggers without lifting.");
        Check(engine.Process(F(C(.9,.97))) is null, "One discrete action per contact remains enforced.");
    }
    private static void Threshold()
    {
        var engine = Engine();
        engine.Process(F(C(.5,.97)));
        for (int i=0;i<100;i++)
            Check(engine.Process(F(C(.5+(i%3-1)*.002,.97))) is null, "Jitter must not accumulate into motion.");
        Check(engine.Process(F(C(.439,.97))) is not null, "Threshold crossing must fire.");
    }

    private static void NavigationOnce()
    {
        var engine = Engine();
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.4,.97)))?.Binding.Gesture == GestureId.BottomSlideLeft, "First swipe.");
        Check(engine.Process(F(C(.2,.97))) is null, "No repeated back.");
        Check(engine.Process(F(C(.8,.97))) is null, "No forward during same cycle.");
        Clear(engine);
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.6,.97)))?.Binding.Gesture == GestureId.BottomSlideRight, "Next cycle.");
    }

    private static void Volume()
    {
        var engine = Engine(new GestureSettings());
        engine.Process(F(C(.97,.8)));
        var invocation = engine.Process(F(C(.97,.66)));
        Check(invocation is { Steps: 3 } && invocation.Binding.Action == GestureAction.VolumeUp, "Large packet carries three steps.");
        for(int i=0;i<10;i++) Check(engine.Process(F(C(.97,.66))) is null, "Stationary must not repeat.");
        invocation = engine.Process(F(C(.97,.71)));
        Check(invocation is { Steps: 1 } && invocation.Binding.Action == GestureAction.VolumeDown, "Reversal.");
        Check(engine.Process(F(C(.97,.72))) is null, "Fractional residue stays below next step.");
    }

    private static void Segmentation()
    {
        int Run(IEnumerable<double> positions)
        {
            var engine = Engine(new GestureSettings());
            engine.Process(F(C(.97,.9)));
            return positions.Select(y=>engine.Process(F(C(.97,y)))?.Steps??0).Sum();
        }
        Check(Run([.5]) == Run(Enumerable.Range(1,40).Select(i=>.9-i*.01)), "Packet segmentation must not change total steps.");
    }

    private static void InvalidContacts()
    {
        foreach (var invalid in new[]
        {
            F(C(.5,.97),C(.6,.97,1)),
            F(C(.5,.97,confidence:false)),
            F(C(.5,.97)) with { Buttons=1u }
        })
        {
            var engine = Engine();
            engine.Process(F(C(.5,.97)));
            engine.Process(invalid);
            Check(engine.Process(F(C(.2,.97))) is null, "Cancelled contact cannot resume.");
            Clear(engine);
            engine.Process(F(C(.5,.97)));
            Check(engine.Process(F(C(.3,.97))) is not null, "Clean cycle works.");
        }
        var unknown = Engine();
        unknown.Process(F(C(.5,.97,confidence:null)));
        Check(unknown.Process(F(C(.3,.97,confidence:null))) is not null, "Absent confidence is not an invented palm flag.");
    }

    private static void Discontinuity()
    {
        var engine = Engine();
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.3,.97,9))) is null, "ID replacement cancels.");
        Clear(engine);
        engine.Process(F(C(.5,.97)));
        engine.Cancel();
        Check(engine.Process(F(C(.3,.97))) is null, "Stream interruption blocks.");
    }

    private static void Corridor()
    {
        var engine = Engine(new GestureSettings());
        engine.Process(F(C(.97,.7)));
        Check(engine.Process(F(C(.7,.7))) is null && !engine.OwnsPointer, "An untriggered exit restores normal motion.");
        Check(engine.Process(F(C(.97,.3))) is null && engine.OwnsPointer, "Re-entry establishes a fresh origin without firing.");
        Check(engine.Process(F(C(.97,.2)))?.Binding.Gesture == GestureId.RightSlideUp, "The new edge swipe can trigger.");
    }
    private static void InitialRelease()
    {
        var engine = new EdgeGestureEngine(All());
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.3,.97))) is null, "Initial contact must finish.");
        Clear(engine);
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.3,.97))) is not null, "Works after all-up.");
    }

    private static void CornerVolume()
    {
        var settings = All() with
        {
            Bindings = All().Bindings.Select(b=>b.Gesture==GestureId.TopLeftInward ? b with {Action=GestureAction.VolumeUp}:b).ToArray()
        };
        var engine = Engine(settings);
        engine.Process(F(C(.03,.03)));
        Check(engine.Process(F(C(.2,.2))) is {Steps:1}, "Corner is discrete.");
        Check(engine.Process(F(C(.4,.4))) is null, "No repeated corner volume.");
    }

    private static void Configuration()
    {
        var settings = new GestureSettings { ObserveOnly=true, EdgeWidth=.115 };
        string json = JsonSerializer.Serialize(settings, GestureSettingsStore.Options);
        var restored = JsonSerializer.Deserialize<GestureSettings>(json,GestureSettingsStore.Options)!;
        restored.Validate();
        Check(restored.EdgeWidth==.115 && restored.Bindings.SequenceEqual(settings.Bindings), "Round trip.");
        string folder=Path.Combine(Path.GetTempPath(),"TouchPadAdvanced-test-"+Guid.NewGuid().ToString("N"));
        string path=Path.Combine(folder,"settings.json");
        try
        {
            GestureSettingsStore.Save(restored,path);
            GestureSettingsStore.Save(restored with {Enabled=false},path);
            var saved=JsonSerializer.Deserialize<GestureSettings>(File.ReadAllText(path),GestureSettingsStore.Options)!;
            Check(!saved.Enabled && !File.Exists(path+".tmp"),"Atomic replacement and cleanup.");
        }
        finally { if(File.Exists(path))File.Delete(path); if(Directory.Exists(folder))Directory.Delete(folder); }
    }

    private static void Validation()
    {
        Reject(()=> (new GestureSettings { EdgeWidth=double.NaN }).Validate());
        Reject(()=> (new GestureSettings { RepeatDistance=0 }).Validate());
        Reject(()=> (new GestureSettings { Bindings=[] }).Validate());
        Reject(()=> (new GestureSettings { Bindings=Enumerable.Repeat(new GestureBinding(GestureId.TopSlideLeft,GestureAction.Back),12).ToArray() }).Validate());
        foreach(string text in new[]{"Ctrl","Ctrl++K","Ctrl+Ctrl+K","Ctrl+K+L","NotAKey","F25"})
            Reject(()=>ShortcutKeys.Parse(text));
        Check(ShortcutKeys.Parse("Ctrl+Shift+T")==new ShortcutKeys(ShortcutModifiers.Ctrl|ShortcutModifiers.Shift,0x54),"Shortcut parse.");
    }

    private static void InputEncoding()
    {
        Check(Marshal.SizeOf<Native.Input>()==40 && Marshal.SizeOf<Native.MouseInput>()==32 &&
            Marshal.SizeOf<Native.KeyboardInput>()==24,"Input ABI.");
        foreach(var (action,button) in new[]{(GestureAction.Back,1u),(GestureAction.Forward,2u)})
        {
            var inputs=GestureActions.Build(new(GestureId.BottomSlideLeft,action));
            Check(inputs.Length==2 && inputs.All(i=>i.Type==0 && i.Mouse.Data==button),"True mouse side button.");
            Check(inputs[0].Mouse.Flags==0x80 && inputs[1].Mouse.Flags==0x100,"Paired X down/up.");
        }
        var volume=GestureActions.Build(new(GestureId.RightSlideUp,GestureAction.VolumeUp),3);
        Check(volume.Length==6 && volume.All(i=>i.Type==1 && i.Keyboard.VirtualKey==0xAF),"Volume key steps.");
    }

    private static void Shortcuts()
    {
        var binding=new GestureBinding(GestureId.TopLeftInward,GestureAction.Shortcut,"Ctrl+Shift+T");
        var inputs=GestureActions.Build(binding);
        Check(inputs.Select(i=>i.Keyboard.VirtualKey).SequenceEqual(new ushort[]{0x11,0x10,0x54,0x54,0x10,0x11}),"Modifier order.");
        Check(inputs.Take(3).All(i=>(i.Keyboard.Flags&2)==0) && inputs.Skip(3).All(i=>(i.Keyboard.Flags&2)!=0),"Key up order.");
        inputs=GestureActions.Build(binding,1,ShortcutModifiers.Ctrl);
        Check(inputs.All(i=>i.Keyboard.VirtualKey!=0x11),"Do not release physically held modifier.");
        inputs=GestureActions.Build(binding with {Shortcut="Alt+Left"});
        Check((inputs[1].Keyboard.Flags&1)!=0,"Navigation extended key.");
    }

    private static void Cleanup()
    {
        var inputs=GestureActions.Build(new(GestureId.TopLeftInward,GestureAction.Shortcut,"Ctrl+Shift+T"));
        Check(GestureActions.PendingReleases(inputs).Length==0,"Complete sequence holds nothing.");
        var releases=GestureActions.PendingReleases(inputs.Take(3));
        Check(releases.Select(i=>i.Keyboard.VirtualKey).SequenceEqual(new ushort[]{0x54,0x10,0x11}) &&
            releases.All(i=>(i.Keyboard.Flags&2)!=0),"Only injected prefix released.");
        var side=GestureActions.Build(new(GestureId.BottomSlideLeft,GestureAction.Back));
        Check(GestureActions.PendingReleases(side.Take(1)).Single().Mouse.Flags==0x100,"Partial side press released.");
    }

    private static void ContinuousScrollAndBrightness()
    {
        var scrollSettings = new GestureSettings
        {
            Bindings = new GestureSettings().Bindings.Select(b => b.Gesture switch
            {
                GestureId.LeftSlideUp => b with { Action = GestureAction.ScrollUp },
                GestureId.LeftSlideDown => b with { Action = GestureAction.ScrollDown },
                GestureId.TopSlideLeft => b with { Action = GestureAction.BrightnessDown },
                GestureId.TopSlideRight => b with { Action = GestureAction.BrightnessUp },
                _ => b
            }).ToArray()
        };
        var engine = Engine(scrollSettings);
        engine.Process(F(C(.03, .8)));
        Check(engine.ActiveOrigin == (.03, .8), "Origin tracked on edge touchdown.");
        var up = engine.Process(F(C(.03, .66)));
        Check(up is { Steps: 3 } && up.Binding.Action == GestureAction.ScrollUp, "Continuous scroll up steps.");
        var down = engine.Process(F(C(.03, .71)));
        Check(down is { Steps: 1 } && down.Binding.Action == GestureAction.ScrollDown, "Continuous scroll reversal.");
        var wheelInputs = GestureActions.Build(new(GestureId.LeftSlideUp, GestureAction.ScrollUp), 2);
        Check(wheelInputs.Length == 2 && wheelInputs.All(i => i.Type == 0 && i.Mouse.Flags == GestureActions.MouseWheelFlag && (int)i.Mouse.Data == 120), "Vertical wheel encoding.");
        var hwheelInputs = GestureActions.Build(new(GestureId.TopSlideLeft, GestureAction.ScrollLeft), 2);
        Check(hwheelInputs.Length == 2 && hwheelInputs.All(i => i.Type == 0 && i.Mouse.Flags == GestureActions.MouseHWheelFlag && (int)i.Mouse.Data == -120), "Horizontal wheel encoding.");
        Check(GestureActions.PendingReleases(wheelInputs).Length == 0, "Wheel events do not leave pending releases.");
        byte[] levels = [0, 10, 25, 50, 75, 100];
        Check(ScreenBrightness.ComputeNextLevel(50, 15, levels) == 75 &&
              ScreenBrightness.ComputeNextLevel(50, -20, levels) == 25 &&
              ScreenBrightness.ComputeNextLevel(95, 20, []) == 100, "Brightness level quantization.");
    }

    private static void TypingGuardAndOrigin()
    {
        var guard = new TypingActivityGuard();
        Check(!guard.PollIsTyping(_ => false, 1000), "Idle keyboard is not typing.");
        Check(guard.PollIsTyping(k => k == 0x41, 1000), "Letter key triggers typing cooldown.");
        Check(guard.PollIsTyping(_ => false, 1200), "Cooldown remains active within 350ms.");
        Check(!guard.PollIsTyping(_ => false, 1400), "Cooldown expires after 350ms.");
        Check(!guard.PollIsTyping(k => k is 0x11 or 0x43, 2000), "Ctrl+C shortcut does not trigger typing guard.");

        using var source = new RawInputSource();
        bool typing = true;
        var dispatched = new List<GestureInvocation>();
        using var runtime = new GestureRuntime(source, new GestureSettings { SuppressWhileTyping = true },
            inv => dispatched.Add(inv), () => typing);
        runtime.ProcessFrame(F());
        runtime.ProcessFrame(F(C(.5, .97)));
        runtime.ProcessFrame(F(C(.3, .97)));
        Check(dispatched.Count == 0 && !runtime.PointerCaptureRequested, "Typing suppresses palm edge touchdown.");
        runtime.ProcessFrame(F());
        typing = false;
        runtime.ProcessFrame(F(C(.5, .97)));
        Check(runtime.ActiveOrigin == (.5, .97), "Runtime exposes active gesture origin.");
        runtime.ProcessFrame(F(C(.3, .97)));
        Check(dispatched.Count == 1 && dispatched[0].Binding.Action == GestureAction.Back, "Normal swipe fires after typing stops.");
    }
}

