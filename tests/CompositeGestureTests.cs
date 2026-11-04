namespace TouchPadAdvanced;

internal static class CompositeGestureTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("Pointer motion can enter all sixteen configured gesture origins without lifting", AllOrigins),
        ("Pointer travel and earlier entries never accumulate into a new swipe", EntryOrigin),
        ("Unbound areas remain free and untriggered exits restore pointer movement", PointerTransitions),
        ("Invalid pointer contacts still require a full release before gesture entry", InvalidPointerContacts),
        ("Volume after pointer motion preserves displacement, segmentation and reversal", EnterVolume),
        ("Pointer-to-edge capture starts on entry before any action is triggered", CaptureTransition)
    ];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static Contact C(double x, double y, int id = 0, bool? confidence = true) =>
        new(id, (int)Math.Round(x * 10000), (int)Math.Round(y * 10000), true, confidence,
            null, null, null, new Axis(0,10000), new Axis(0,10000));
    private static TouchFrame F(params Contact[] contacts) => new(DateTimeOffset.UnixEpoch, 0, 0, contacts);

    private static void AllOrigins()
    {
        var settings = new GestureSettings
        {
            Bindings = GestureCatalog.All.Select(s => new GestureBinding(s.Id, GestureAction.Back)).ToArray()
        };
        var cases = new (GestureId Id, double X, double Y, double Ex, double Ey)[]
        {
            (GestureId.TopSlideLeft,.5,.03,.35,.03), (GestureId.TopSlideRight,.5,.03,.65,.03),
            (GestureId.BottomSlideLeft,.5,.97,.35,.97), (GestureId.BottomSlideRight,.5,.97,.65,.97),
            (GestureId.LeftSlideUp,.03,.5,.03,.35), (GestureId.LeftSlideDown,.03,.5,.03,.65),
            (GestureId.RightSlideUp,.97,.5,.97,.35), (GestureId.RightSlideDown,.97,.5,.97,.65),
            (GestureId.TopInward,.5,.03,.5,.23), (GestureId.BottomInward,.5,.97,.5,.77),
            (GestureId.LeftInward,.03,.5,.23,.5), (GestureId.RightInward,.97,.5,.77,.5),
            (GestureId.TopLeftInward,.03,.03,.18,.18), (GestureId.TopRightInward,.97,.03,.82,.18),
            (GestureId.BottomLeftInward,.03,.97,.18,.82), (GestureId.BottomRightInward,.97,.97,.82,.82)
        };
        foreach (var c in cases)
        {
            var engine = new EdgeGestureEngine(settings, false);
            Check(engine.Process(F(C(.5,.5))) is null && !engine.OwnsPointer, "Center is ordinary pointer input.");
            engine.Process(F(C(.6,.6)));
            Check(engine.Process(F(C(c.X,c.Y))) is null && engine.OwnsPointer, c.Id + ": entry captures without triggering.");
            Check(engine.Process(F(C(c.Ex,c.Ey)))?.Binding.Gesture == c.Id, c.Id + ": swipe after entry.");
            Check(engine.Process(F(C(c.X,c.Y))) is null, "Discrete action still fires once per touch.");
            engine.Process(F());
            Check(!engine.OwnsPointer, "Lift always releases the pointer.");
        }
    }

    private static void EntryOrigin()
    {
        var engine = new EdgeGestureEngine(new GestureSettings(), false);
        engine.Process(F(C(.7,.5)));
        engine.Process(F(C(.5,.7)));
        Check(engine.Process(F(C(.3,.97))) is null, "A large approach displacement cannot fire Back.");
        for (int i = 0; i < 20; i++)
            Check(engine.Process(F(C(.3 + (i % 2) * .001,.97))) is null, "Stationary edge jitter does not accumulate.");
        Check(engine.Process(F(C(.28,.97))) is null, "Only travel after entry counts.");
        Check(engine.Process(F(C(.7,.6))) is null && !engine.OwnsPointer, "Abandon an untriggered candidate.");
        Check(engine.Process(F(C(.6,.97))) is null, "The next entry has its own origin.");
        Check(engine.Process(F(C(.58,.97))) is null, "Earlier edge travel cannot carry into a new entry.");
        Check(engine.Process(F(C(.539,.97)))?.Binding.Gesture == GestureId.BottomSlideLeft, "New threshold crosses normally.");
    }

    private static void PointerTransitions()
    {
        var engine = new EdgeGestureEngine(new GestureSettings(), false);
        foreach (var c in new[] { C(.5,.5), C(.03,.5), C(.5,.03), C(.03,.03), C(.5,.5) })
            Check(engine.Process(F(c)) is null && !engine.OwnsPointer, "Center and unbound regions remain ordinary input.");
        engine.Process(F(C(.5,.97)));
        Check(engine.OwnsPointer, "Bound bottom edge is captured.");
        Check(engine.Process(F(C(.5,.915))) is null && engine.OwnsPointer, "The corridor tolerates small border drift.");
        Check(engine.Process(F(C(.5,.88))) is null && !engine.OwnsPointer, "An abandoned swipe returns to ordinary input.");
        Check(engine.Phase == GesturePhase.Pointer, "Pointer tracking resumes without resetting contact identity.");
        engine.Process(F(C(.97,.6)));
        Check(engine.OwnsPointer && engine.Process(F(C(.97,.5)))?.Binding.Gesture == GestureId.RightSlideUp,
            "The same finger can move to another edge before any action has fired.");
    }

    private static void InvalidPointerContacts()
    {
        var invalid = new[]
        {
            F(C(.5,.5),C(.6,.5,1)), F(C(.5,.5,confidence:false)),
            F(C(.5,.5)) with { Buttons = 1 }, F(C(.5,.5,9)), F(C(1.1,.5))
        };
        foreach (var frame in invalid)
        {
            var engine = new EdgeGestureEngine(new GestureSettings(), false);
            engine.Process(F(C(.5,.5)));
            engine.Process(frame);
            Check(engine.Process(F(C(.5,.97))) is null && !engine.OwnsPointer,
                "Invalid input cannot become a new edge contact while a finger remains down.");
            engine.Process(F());
            engine.Process(F(C(.5,.5)));
            engine.Process(F(C(.5,.97)));
            Check(engine.Process(F(C(.4,.97)))?.Binding.Gesture == GestureId.BottomSlideLeft,
                "A clean release restores composition.");
        }
    }

    private static void EnterVolume()
    {
        int Run(IEnumerable<double> positions)
        {
            var engine = new EdgeGestureEngine(new GestureSettings(), false);
            engine.Process(F(C(.5,.5)));
            Check(engine.Process(F(C(.97,.9))) is null, "Approaching the volume edge cannot change volume.");
            return positions.Sum(y => engine.Process(F(C(.97,y)))?.Steps ?? 0);
        }
        Check(Run([.5]) == Run(Enumerable.Range(1,40).Select(i => .9 - i * .01)),
            "Packet segmentation remains irrelevant after entry.");
        var engine = new EdgeGestureEngine(new GestureSettings(), false);
        engine.Process(F(C(.5,.5)));
        engine.Process(F(C(.97,.8)));
        Check(engine.Process(F(C(.97,.66))) is { Steps: 3 }, "The full volume displacement is measured from entry.");
        Check(engine.Process(F(C(.97,.71)))?.Binding.Action == GestureAction.VolumeDown, "Volume can reverse.");
    }

    private static void CaptureTransition()
    {
        var settings = new GestureSettings();
        var engine = new EdgeGestureEngine(settings, false);
        var mouse = new MouseObservation(0x200,0,0,0,0,0,0,0,true,0);
        bool Captured() => PointerCapturePolicy.ShouldSuppress(mouse,
            PointerCapturePolicy.IsRequested(settings, false, engine.OwnsPointer));
        engine.Process(F(C(.5,.5)));
        Check(!Captured(), "Ordinary movement passes through.");
        Check(engine.Process(F(C(.5,.97))) is null && Captured(), "Entry captures before any gesture fires.");
        Check(engine.Process(F(C(.49,.97))) is null && Captured(), "Subthreshold edge movement does not leak to the cursor.");
        engine.Process(F(C(.5,.8)));
        Check(!Captured(), "Retreating from the candidate releases capture.");
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.4,.97))) is not null && Captured(), "Capture remains active while the action is dispatched.");
        engine.Process(F());
        Check(!Captured(), "Release ends capture.");
    }
}
