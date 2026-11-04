using System.Text.Json;
using System.Text.Json.Nodes;

namespace TouchPadAdvanced;

internal static class SelectionTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("Task selection tracks position, reversal and boundary hysteresis until lift", TaskPosition),
        ("Task direction and edge-inward or corner projections drive the same session", SelectionOrigins),
        ("Invalid contact and explicit cancellation terminate a selection once", SelectionCancellation),
        ("Native task encoder holds Alt while selecting and confirms only on lift", TaskInputs),
        ("Cancelling task selection dismisses the switcher before releasing owned keys", CancelInputs),
        ("Desktop selection moves continuously in both directions without held modifiers", DesktopInputs),
        ("Partial selection injection cancels and releases only the successful prefix", PartialInputs),
        ("Physical modifier keys are never borrowed or released by a selection", PhysicalModifiers),
        ("Runtime pause, settings change and disposal cancel active task selection", RuntimeLifecycle),
        ("Runtime input failure cancels capture and observation sends no OS input", RuntimeFailure),
        ("Selection step configuration round trips and rejects invalid ranges", Configuration)
    ];
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static Contact C(double x, double y, int id = 0, bool? confidence = true) =>
        new(id,(int)Math.Round(x*10000),(int)Math.Round(y*10000),true,confidence,null,null,null,
            new Axis(0,10000),new Axis(0,10000));
    private static TouchFrame F(params Contact[] contacts) => new(DateTimeOffset.UnixEpoch,0,0,contacts);
    private static GestureSettings Settings(GestureId id = GestureId.BottomSlideRight,
        GestureAction action = GestureAction.SwitchApp) => new()
    {
        SelectionDistance = .1,
        Bindings = GestureCatalog.All.Select(s => new GestureBinding(s.Id,
            s.Id == id ? action : GestureAction.None)).ToArray()
    };
    private static GestureInvocation Event(GestureInteraction phase, int offset = 0,
        GestureAction action = GestureAction.SwitchApp) =>
        new(new GestureBinding(GestureId.BottomSlideRight,action),Interaction:phase,SelectionOffset:offset);
    private static bool Down(Native.Input input, ushort key) =>
        input.Type == 1 && input.Keyboard.VirtualKey == key && (input.Keyboard.Flags & 2) == 0;

    private static void TaskPosition()
    {
        var engine = new EdgeGestureEngine(Settings(),false);
        engine.Process(F(C(.5,.5)));
        Check(engine.Process(F(C(.5,.97))) is null && engine.OwnsPointer, "Entry captures without opening the switcher.");
        Check(engine.Process(F(C(.58,.97))) is { Interaction: GestureInteraction.Begin, SelectionOffset: 1 }, "Begin selection.");
        Check(engine.Process(F(C(.79,.97))) is { Interaction: GestureInteraction.Update, SelectionOffset: 3 }, "Position selects multiple entries.");
        Check(engine.Process(F(C(.79,.97))) is null, "Holding still does not repeat.");
        Check(engine.Process(F(C(.755,.97))) is null, "A return margin prevents boundary flicker.");
        Check(engine.Process(F(C(.74,.97))) is { Interaction: GestureInteraction.Update, SelectionOffset: 2 }, "Reverse to the previous entry.");
        Check(engine.Process(F(C(.5,.97))) is { Interaction: GestureInteraction.Update, SelectionOffset: 0 } && engine.OwnsPointer,
            "Returning to the origin keeps the switcher open on the original task.");
        Check(engine.Process(F(C(.4,.97))) is { Interaction: GestureInteraction.Update, SelectionOffset: -1 }, "Selection can cross the origin.");
        Check(engine.Process(F()) is { Interaction: GestureInteraction.Commit, SelectionOffset: -1 } && !engine.OwnsPointer, "Lift confirms and releases capture.");
        Check(engine.Process(F()) is null, "Repeated empty frames do not confirm again.");
    }

    private static void SelectionOrigins()
    {
        var cases = new (GestureId Id, double X, double Y, double Ex, double Ey)[]
        {
            (GestureId.BottomSlideRight,.5,.97,.58,.97),
            (GestureId.LeftSlideUp,.03,.5,.03,.42),
            (GestureId.TopInward,.5,.03,.5,.11),
            (GestureId.TopLeftInward,.03,.03,.09,.09)
        };
        foreach (var action in new[] { GestureAction.SwitchApp, GestureAction.PreviousTask,
            GestureAction.NextDesktop, GestureAction.PreviousDesktop, GestureAction.AppSwitcher })
        foreach (var c in cases)
        {
            var engine = new EdgeGestureEngine(Settings(c.Id,action),false);
            engine.Process(F(C(c.X,c.Y)));
            var begin = engine.Process(F(C(c.Ex,c.Ey)));
            Check(begin?.Interaction == GestureInteraction.Begin &&
                begin.SelectionOffset == GestureCatalog.SelectionDirection(action), "All origins respect action direction.");
            Check(engine.Process(F())?.Interaction == GestureInteraction.Commit, "All origins share the lift lifecycle.");
        }
    }

    private static void SelectionCancellation()
    {
        var invalid = new[]
        {
            F(C(.6,.97),C(.7,.97,1)), F(C(.6,.97,confidence:false)),
            F(C(.6,.97,9)), F(C(.6,.97)) with {Buttons=1}, F(C(1.1,.97))
        };
        foreach (var frame in invalid)
        {
            var engine = new EdgeGestureEngine(Settings(),false);
            engine.Process(F(C(.5,.97)));
            engine.Process(F(C(.6,.97)));
            Check(engine.Process(frame)?.Interaction == GestureInteraction.Cancel && !engine.OwnsPointer,
                "An invalid frame cancels an active selection.");
            Check(engine.Process(F(C(.7,.97))) is null, "Cancelled contacts cannot resume.");
            Check(engine.Process(F()) is null, "Cancellation cannot later become a commit.");
        }
        var explicitCancel = new EdgeGestureEngine(Settings(),false);
        explicitCancel.Process(F(C(.5,.97)));
        explicitCancel.Process(F(C(.6,.97)));
        Check(explicitCancel.Cancel()?.Interaction == GestureInteraction.Cancel && explicitCancel.Cancel() is null,
            "Explicit cancellation is idempotent.");
    }

    private static void TaskInputs()
    {
        var sent = new List<Native.Input>();
        using var executor = new GestureActionExecutor(inputs => {sent.AddRange(inputs); return (uint)inputs.Length;},_=>false);
        executor.Execute(Event(GestureInteraction.Begin,2));
        Check(executor.IsActive && executor.OwnedKeyCount == 1 &&
            sent.Count(i=>Down(i,0x12)) == 1 && sent.Count(i=>Down(i,0x09)) == 2, "Alt stays held and the switcher starts at the requested entry.");
        int before = sent.Count;
        executor.Execute(Event(GestureInteraction.Update,3));
        Check(sent.Count == before+2, "Advancing one entry sends one Tab pair.");
        executor.Execute(Event(GestureInteraction.Update,1));
        Check(sent.Count(i=>Down(i,0x10)) == 1 && executor.OwnedKeyCount == 1, "Reverse selection uses a balanced Shift batch.");
        executor.Execute(Event(GestureInteraction.Commit,1));
        Check(!executor.IsActive && executor.OwnedKeyCount == 0 &&
            GestureActions.PendingReleases(sent).Length == 0 && !sent.Any(i=>Down(i,0x1B)), "Lift releases Alt to confirm, with no leftover keys.");
    }

    private static void CancelInputs()
    {
        var sent = new List<Native.Input>();
        var executor = new GestureActionExecutor(inputs=>{sent.AddRange(inputs);return(uint)inputs.Length;},_=>false);
        executor.Execute(Event(GestureInteraction.Begin,1));
        executor.Dispose();
        Check(sent.Any(i=>Down(i,0x1B)) && sent[^1].Keyboard.VirtualKey == 0x12 &&
            (sent[^1].Keyboard.Flags&2)!=0 && executor.OwnedKeyCount==0, "Cancel dismisses before releasing Alt.");
        int count = sent.Count;
        executor.Dispose();
        Check(sent.Count == count, "Disposal does not repeat input.");
    }

    private static void DesktopInputs()
    {
        var sent = new List<Native.Input>();
        using var executor = new GestureActionExecutor(inputs=>{sent.AddRange(inputs);return(uint)inputs.Length;},_=>false);
        executor.Execute(Event(GestureInteraction.Begin,-1,GestureAction.PreviousDesktop));
        executor.Execute(Event(GestureInteraction.Update,-3,GestureAction.PreviousDesktop));
        executor.Execute(Event(GestureInteraction.Update,-1,GestureAction.PreviousDesktop));
        Check(sent.Count(i=>Down(i,0x25))==3 && sent.Count(i=>Down(i,0x27))==2,
            "Desktop movement follows signed displacement, including reversal.");
        Check(executor.OwnedKeyCount==0 && GestureActions.PendingReleases(sent).Length==0, "Desktop batches leave no held modifiers.");
        int count=sent.Count;
        executor.Execute(Event(GestureInteraction.Commit,-1,GestureAction.PreviousDesktop));
        Check(sent.Count==count && !executor.IsActive, "Ending desktop selection sends no duplicate switch.");
    }

    private static void PartialInputs()
    {
        foreach (int target in new[] {2,-2})
        {
            int length = target > 0 ? 4 : 6;
            for (int prefix = 0; prefix < length; prefix++)
            {
                var delivered = new List<Native.Input>();
                int call = 0;
                using var executor = new GestureActionExecutor(inputs =>
                {
                    uint count = ++call == 2 ? (uint)prefix : (uint)inputs.Length;
                    delivered.AddRange(inputs.Take((int)count));
                    return count;
                }, _ => false);
                bool rejected = false;
                try { executor.Execute(Event(GestureInteraction.Begin,target)); }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected && executor.OwnedKeyCount == 0 && !executor.IsActive,
                    "Every incomplete forward/reverse prefix cleans its ownership.");
                int escape = delivered.FindIndex(i => Down(i,0x1B));
                int altRelease = delivered.FindIndex(i => i.Keyboard.VirtualKey == 0x12 && (i.Keyboard.Flags & 2) != 0);
                Check(escape >= 0 && altRelease > escape, "Failed selection cancels before releasing Alt.");
                Check(GestureActions.PendingReleases(delivered.Take(escape)).Select(i => i.Keyboard.VirtualKey)
                    .SequenceEqual(new ushort[] {0x12}), "Transient Shift and Tab are released before cancellation.");
                Check(GestureActions.PendingReleases(delivered).Length == 0,
                    "Only successfully injected keys are released, with no leftover key.");
            }
        }
    }
    private static void PhysicalModifiers()
    {
        foreach (int key in new[]{0x10,0x11,0x12,0x5B,0x5C,0x09})
        {
            int sent=0;
            using var executor=new GestureActionExecutor(inputs=>{sent+=inputs.Length;return(uint)inputs.Length;},k=>k==key);
            bool rejected=false;
            try {executor.Execute(Event(GestureInteraction.Begin,1));}catch(InvalidOperationException){rejected=true;}
            Check(rejected && sent==0 && executor.OwnedKeyCount==0, "A physical modifier is never taken over.");
        }
    }

    private static void Start(GestureRuntime runtime)
    {
        runtime.ProcessFrame(F());
        runtime.ProcessFrame(F(C(.5,.97)));
        runtime.ProcessFrame(F(C(.6,.97)));
    }

    private static void RuntimeLifecycle()
    {
        foreach (string mode in new[]{"pause","settings","dispose","release"})
        {
            using var source=new RawInputSource();
            var events=new List<(GestureInteraction Phase,bool Capture)>();
            GestureRuntime? runtime=null;
            runtime=new GestureRuntime(source,Settings(),inv=>events.Add((inv.Interaction,runtime!.PointerCaptureRequested)));
            try
            {
                Start(runtime);
                Check(events.Single()==(GestureInteraction.Begin,true),"Capture is reserved before the switcher opens.");
                switch(mode)
                {
                    case "pause": runtime.SetPaused(true); break;
                    case "settings": runtime.Apply(Settings() with {ObserveOnly=true}); break;
                    case "dispose": runtime.Dispose(); break;
                    default: runtime.ProcessFrame(F()); break;
                }
                var expected=mode=="release"?GestureInteraction.Commit:GestureInteraction.Cancel;
                Check(events[^1]==(expected,false),"Lifecycle ends input only after releasing capture.");
            }
            finally {runtime.Dispose();}
        }
    }

    private static void RuntimeFailure()
    {
        using var source=new RawInputSource();
        var events=new List<GestureInteraction>();
        using(var runtime=new GestureRuntime(source,Settings(),inv=>
        {
            events.Add(inv.Interaction);
            if(inv.Interaction==GestureInteraction.Update)throw new InvalidOperationException("test failure");
        }))
        {
            Start(runtime);
            runtime.ProcessFrame(F(C(.8,.97)));
            Check(events.SequenceEqual(new[]{GestureInteraction.Begin,GestureInteraction.Update,GestureInteraction.Cancel}) &&
                !runtime.PointerCaptureRequested && runtime.Activities.Any(a=>!a.Succeeded),"A failed update aborts input and capture.");
        }
        int calls=0;
        using var observe=new GestureRuntime(source,Settings() with {ObserveOnly=true},_=>calls++);
        Start(observe);
        observe.ProcessFrame(F(C(.8,.97)));
        observe.ProcessFrame(F());
        Check(calls==0 && !observe.PointerCaptureRequested,"Observation never sends selection input.");
    }

    private static void Configuration()
    {
        var settings=Settings() with {SelectionDistance=.17};
        string json=JsonSerializer.Serialize(settings,GestureSettingsStore.Options);
        Check(JsonSerializer.Deserialize<GestureSettings>(json,GestureSettingsStore.Options)!.SelectionDistance==.17,"Selection sensitivity persists.");
        var legacy=JsonNode.Parse(json)!.AsObject();
        legacy.Remove("SelectionDistance");
        legacy.Remove("selectionDistance");
        Check(JsonSerializer.Deserialize<GestureSettings>(legacy.ToJsonString(),GestureSettingsStore.Options)!.SelectionDistance==.12,
            "Legacy settings acquire the default selection step.");
        foreach(double value in new[]{double.NaN,0,.01,.31,double.PositiveInfinity})
        {
            bool rejected=false;
            try{(settings with {SelectionDistance=value}).Validate();}catch(InvalidDataException){rejected=true;}
            Check(rejected,"Invalid selection steps cannot reach the recognizer.");
        }
    }
}
