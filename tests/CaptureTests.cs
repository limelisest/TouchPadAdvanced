using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

internal static class CaptureTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("Pointer is claimed on edge touchdown before the trigger threshold", CandidateOwnership),
        ("Pointer remains claimed after firing and releases on lift", ClaimedLifecycle),
        ("Center and unbound zones stay free until entering a bound edge", ExcludedOwnership),
        ("Cancelling a contact releases pointer ownership immediately", CancelOwnership),
        ("Capture mode gates exclude pause, observe-only and disabled recognition", ModeGates),
        ("Only physical cursor motion is intercepted; buttons and injected actions pass", MousePolicy),
        ("Independent edge bands affect all four origins and corridors", IndependentBands),
        ("Corner regions use the intersection of their two adjacent edge bands", CornerBands),
        ("Legacy unified widths migrate without changing their effective regions", LegacyBands),
        ("Independent edge ranges persist and invalid ranges are rejected", BandConfiguration),
        ("Windows built-in actions encode the documented paired shortcuts", WindowsActions),
        ("Dedicated input host marshals commands and shuts hooks down cleanly", InputThread),
        ("Windows low-level hook consumes a tagged movement event before forwarding", NativeHook)
    ];

    private static void Check(bool condition,string reason)
    {
        if(!condition)throw new InvalidOperationException(reason);
    }
    private static Contact C(double x,double y,int id=0,bool? confidence=true) =>
        new(id,(int)Math.Round(x*10000),(int)Math.Round(y*10000),true,confidence,null,null,null,
            new Axis(0,10000),new Axis(0,10000));
    private static TouchFrame F(params Contact[] contacts)=>new(DateTimeOffset.UnixEpoch,0,0,contacts);
    private static GestureSettings All()=>new()
    {
        Bindings=GestureCatalog.All.Select(s=>new GestureBinding(s.Id,GestureAction.Back)).ToArray()
    };

    private static void CandidateOwnership()
    {
        var engine=new EdgeGestureEngine(new GestureSettings(),false);
        engine.Process(F(C(.5,.97)));
        Check(engine.OwnsPointer,"Must claim before sliding, not only after triggering.");
        Check(engine.Process(F(C(.49,.97))) is null && engine.OwnsPointer,"Candidate motion stays captured.");
    }
    private static void ClaimedLifecycle()
    {
        var engine=new EdgeGestureEngine(new GestureSettings(),false);
        engine.Process(F(C(.5,.97)));
        Check(engine.Process(F(C(.3,.97))) is not null && engine.OwnsPointer,"Triggered gesture remains captured.");
        engine.Process(F(C(.4,.97)));
        Check(engine.OwnsPointer,"Consumed motion remains captured.");
        engine.Process(F());
        Check(!engine.OwnsPointer,"Lift releases the pointer.");
    }
    private static void ExcludedOwnership()
    {
        foreach(var c in new[]{C(.5,.5),C(.5,.03),C(.5,.97,confidence:false)})
        {
            var engine=new EdgeGestureEngine(new GestureSettings(),false);
            engine.Process(F(c));
            Check(!engine.OwnsPointer,"Unbound, center or rejected contact captured.");
        }
        var center=new EdgeGestureEngine(new GestureSettings(),false);
        center.Process(F(C(.5,.5)));
        center.Process(F(C(.5,.97)));
        Check(center.OwnsPointer,"Center-to-edge must claim the pointer on the entry frame.");
    }
    private static void CancelOwnership()
    {
        foreach(var frame in new[]{F(C(.5,.97),C(.6,.97,1)),F(C(.5,.97,9)),F(C(.5,.97)) with {Buttons=1}})
        {
            var engine=new EdgeGestureEngine(new GestureSettings(),false);
            engine.Process(F(C(.5,.97)));
            engine.Process(frame);
            Check(!engine.OwnsPointer,"Invalid contact must release capture.");
        }
        var interrupted=new EdgeGestureEngine(new GestureSettings(),false);
        interrupted.Process(F(C(.5,.97)));
        interrupted.Cancel();
        Check(!interrupted.OwnsPointer,"Stream interruption releases capture.");
    }
    private static void ModeGates()
    {
        var settings=new GestureSettings();
        Check(PointerCapturePolicy.IsRequested(settings,false,true),"Execution candidate must capture.");
        Check(!PointerCapturePolicy.IsRequested(settings,true,true),"Pause.");
        Check(!PointerCapturePolicy.IsRequested(settings with {ObserveOnly=true},false,true),"Observe.");
        Check(!PointerCapturePolicy.IsRequested(settings with {Enabled=false},false,true),"Disabled.");
        Check(!PointerCapturePolicy.IsRequested(settings with {CapturePointer=false},false,true),"Capture toggle.");
        Check(!PointerCapturePolicy.IsRequested(settings,false,false),"No owner.");
    }
    private static void MousePolicy()
    {
        var motion=new MouseObservation(0x200,0,0,0,0,0,0,0,true,0);
        Check(PointerCapturePolicy.ShouldSuppress(motion,true),"Physical motion.");
        Check(!PointerCapturePolicy.ShouldSuppress(motion,false),"Normal motion.");
        Check(!PointerCapturePolicy.ShouldSuppress(motion with {Flags=1},true),"Injected motion.");
        Check(!PointerCapturePolicy.ShouldSuppress(motion with {Flags=2},true),"Lower-integrity injected motion.");
        Check(!PointerCapturePolicy.ShouldSuppress(motion with {ExtraInfo=(ulong)GestureActions.InputTag},true),"Own injection.");
        foreach(uint message in new uint[]{0x201,0x202,0x204,0x205,0x20A,0x20B,0x20C})
            Check(!PointerCapturePolicy.ShouldSuppress(motion with {Message=message},true),"Buttons and wheel must pass.");
    }
    private static void IndependentBands()
    {
        var settings=All() with {EdgeRanges=new(.04,.16,.06,.12)};
        var cases=new(double,double,double,double,GestureId)[]
        {
            (.5,.03,.65,.03,GestureId.TopSlideRight),(.5,.85,.65,.85,GestureId.BottomSlideRight),
            (.05,.5,.05,.35,GestureId.LeftSlideUp),(.89,.5,.89,.35,GestureId.RightSlideUp)
        };
        foreach(var(x,y,ex,ey,id)in cases)
        {
            var engine=new EdgeGestureEngine(settings,false);
            engine.Process(F(C(x,y)));
            Check(engine.OwnsPointer && engine.Process(F(C(ex,ey)))?.Binding.Gesture==id,id.ToString());
        }
        foreach(var c in new[]{C(.5,.05),C(.5,.83),C(.07,.5),C(.87,.5)})
        {
            var engine=new EdgeGestureEngine(settings,false);
            engine.Process(F(c));
            Check(!engine.OwnsPointer,"Outside the corresponding individual edge band.");
        }
    }
    private static void CornerBands()
    {
        var settings=All() with {EdgeRanges=new(.04,.16,.06,.12)};
        var engine=new EdgeGestureEngine(settings,false);
        engine.Process(F(C(.90,.86)));
        Check(engine.Process(F(C(.79,.75)))?.Binding.Gesture==GestureId.BottomRightInward,"Asymmetric bottom-right intersection.");
        engine.Process(F());
        engine.Process(F(C(.05,.03)));
        Check(engine.Process(F(C(.15,.13)))?.Binding.Gesture==GestureId.TopLeftInward,"Asymmetric top-left intersection.");
    }
    private static void LegacyBands()
    {
        var settings=new GestureSettings {EdgeWidth=.14};
        foreach(var edge in Enum.GetValues<PadEdge>())
            Check(settings.GetEdgeWidth(edge)==.14,"Legacy width must initialize each edge.");
        var json=System.Text.Json.JsonSerializer.Serialize(settings,GestureSettingsStore.Options);
        var restored=System.Text.Json.JsonSerializer.Deserialize<GestureSettings>(json,GestureSettingsStore.Options)!;
        Check(restored.EdgeRanges is null && restored.GetEdgeWidth(PadEdge.Left)==.14,"Old JSON behavior.");
    }
    private static void BandConfiguration()
    {
        var settings=new GestureSettings {EdgeRanges=new(.03,.13,.05,.17)};
        settings.Validate();
        var json=System.Text.Json.JsonSerializer.Serialize(settings,GestureSettingsStore.Options);
        var restored=System.Text.Json.JsonSerializer.Deserialize<GestureSettings>(json,GestureSettingsStore.Options)!;
        Check(restored.EdgeRanges==settings.EdgeRanges,"Persist independent widths.");
        foreach(var ranges in new[]{new EdgeRecognitionRanges(Top:double.NaN),new EdgeRecognitionRanges(Bottom:.21),new EdgeRecognitionRanges(Left:0),new EdgeRecognitionRanges(Right:double.PositiveInfinity)})
        {
            try{(settings with {EdgeRanges=ranges}).Validate();}
            catch(InvalidDataException){continue;}
            throw new InvalidOperationException("Invalid independent width accepted.");
        }
    }
    private static void WindowsActions()
    {
        var expected=new Dictionary<GestureAction,string>
        {
            [GestureAction.SwitchApp]="Alt+Tab",[GestureAction.AppSwitcher]="Ctrl+Alt+Tab",
            [GestureAction.TaskView]="Win+Tab",[GestureAction.StartMenu]="Ctrl+Esc",
            [GestureAction.ShowDesktop]="Win+D",[GestureAction.PreviousDesktop]="Win+Ctrl+Left",
            [GestureAction.NextDesktop]="Win+Ctrl+Right",[GestureAction.QuickSettings]="Win+A",
            [GestureAction.Notifications]="Win+N",[GestureAction.FileExplorer]="Win+E"
        };
        foreach(var(action,shortcut)in expected)
        {
            Check(GestureActions.BuiltInShortcut(action)==shortcut,action.ToString());
            var inputs=GestureActions.Build(new(GestureId.TopSlideLeft,action));
            Check(inputs.Length>=4 && inputs.All(i=>i.Type==1),"Keyboard shortcut encoding.");
            Check(GestureActions.PendingReleases(inputs).Length==0,"Modifiers must not stick.");
        }
    }
    private static void InputThread()
    {
        using var host=new GestureInputHost(new GestureSettings {Enabled=false,ObserveOnly=true});
        Check(host.IsRunning && host.Capture.IsInstalled && !host.Source.IsOwnerThread,"Native input threads.");
        using var done=new ManualResetEventSlim();
        Exception? failure=null;
        var settings=new GestureSettings {ObserveOnly=true,EdgeRanges=new(.03,.13,.05,.17)};
        host.Runtime.Apply(settings);
        host.Runtime.SetPaused(true);
        host.Source.Invoke(()=>
        {
            try
            {
                Check(host.Source.IsOwnerThread,"Commands execute on the input owner.");
                Check(host.Runtime.Settings.EdgeRanges==settings.EdgeRanges && host.Runtime.Paused,"FIFO configuration commands.");
                Check(!host.Runtime.PointerCaptureRequested,"Observation and pause never capture.");
            }
            catch(Exception ex){failure=ex;}
            finally{done.Set();}
        });
        Check(done.Wait(TimeSpan.FromSeconds(3)),"Input thread did not process commands.");
        if(failure is not null)throw failure;
    }
    private static void NativeHook()
    {
        const nuint marker=0x54505431;
        int intercepted=0;
        Native.PeekMessage(out _,0,0,0,0);
        using var hook=new MouseInputHook(mouse=>
        {
            if(mouse.ExtraInfo!=(ulong)marker)return false;
            intercepted++;
            return true;
        });
        var input=new Native.Input
        {
            Mouse=new Native.MouseInput {X=1,Flags=1,ExtraInfo=marker} // The test hook consumes this tagged move.
        };
        uint sent=Native.SendInput(1,[input],Marshal.SizeOf<Native.Input>());
        var watch=Stopwatch.StartNew();
        while(intercepted==0 && watch.ElapsedMilliseconds<1000)
        {
            if(Native.PeekMessage(out var message,0,0,0,1))
            {Native.TranslateMessage(ref message);Native.DispatchMessage(ref message);}
            else Thread.Sleep(1);
        }
        Check(sent==1 && intercepted==1,$"Native interception test: sent={sent}, intercepted={intercepted}, error={Marshal.GetLastWin32Error()}.");
    }
}
