namespace TouchPadAdvanced;

public sealed record GestureActivity(DateTimeOffset At, GestureId Gesture, GestureAction Action,
    int Steps, bool Observed, bool Succeeded, string Detail);

public sealed class TypingActivityGuard
{
    public const long CooldownMs = 350;
    private long lastTypingTick = -CooldownMs * 2;
    private static readonly ushort[] TypingKeys =
        Enumerable.Range(0x30, 10).Concat(Enumerable.Range(0x41, 26))
        .Concat([0x08, 0x0D, 0x20, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE])
        .Select(k => (ushort)k).ToArray();

    public bool PollIsTyping(Func<int, bool>? isKeyDown = null, long? nowTick = null)
    {
        var held = isKeyDown ?? (k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
        long now = nowTick ?? Environment.TickCount64;
        // Shortcut combinations with Ctrl/Alt/Win are not ordinary typing.
        if (!held(0x11) && !held(0x12) && !held(0x5B) && !held(0x5C))
        {
            foreach (ushort key in TypingKeys)
            {
                if (held(key))
                {
                    Volatile.Write(ref lastTypingTick, now);
                    return true;
                }
            }
        }
        return now - Volatile.Read(ref lastTypingTick) < CooldownMs;
    }
}

public sealed class GestureRuntime : IDisposable
{
    private readonly RawInputSource source;
    private EdgeGestureEngine engine;
    private readonly Action<GestureInvocation> execute;
    private readonly GestureActionExecutor? actionExecutor;
    private readonly Func<bool>? isTyping;
    private readonly Queue<GestureActivity> activities = new();
    private int pointerCaptureRequested;
    private (double X, double Y)? activeOriginSnapshot;
    public GestureSettings Settings { get; private set; }
    public TouchpadDevice? Device { get; private set; }
    public bool Paused { get; private set; }
    public GesturePhase Phase => engine.Phase;
    public (double X, double Y)? ActiveOrigin => activeOriginSnapshot;
    public bool PointerCaptureRequested => Volatile.Read(ref pointerCaptureRequested) != 0;
    private GestureActivity[] activitySnapshot = [];
    public IReadOnlyCollection<GestureActivity> Activities => Volatile.Read(ref activitySnapshot);
    public long ActivityVersion { get; private set; }

    public GestureRuntime(RawInputSource source, GestureSettings settings,
        Action<GestureInvocation>? execute = null, Func<bool>? isTyping = null)
    {
        this.source = source;
        if (execute is null) actionExecutor = new GestureActionExecutor();
        this.execute = execute ?? actionExecutor!.Execute;
        this.isTyping = isTyping;
        Settings = settings;
        engine = new EdgeGestureEngine(settings);
        Device = source.Devices.FirstOrDefault(d => d.Connected && d.CanDecodeContacts);
        source.FrameReceived += OnFrame;
        source.DeviceInterrupted += OnInterrupted;
    }

    public void SelectDevice(TouchpadDevice? device)
    {
        if (!source.IsOwnerThread) { source.Invoke(() => SelectDevice(device)); return; }
        if (Device == device) return;
        Device = device;
        AbortGesture();
    }

    public void Apply(GestureSettings settings)
    {
        if (!source.IsOwnerThread) { settings.Validate(); source.Invoke(() => Apply(settings)); return; }
        settings.Validate();
        AbortGesture();
        Settings = settings;
        engine = new EdgeGestureEngine(settings);
    }

    public void SetPaused(bool paused)
    {
        if (!source.IsOwnerThread) { source.Invoke(() => SetPaused(paused)); return; }
        if (Paused == paused) return;
        Paused = paused;
        AbortGesture();
    }

    private void OnInterrupted(TouchpadDevice device)
    {
        if (Device == device) AbortGesture();
    }

    private void AbortGesture()
    {
        var cancellation = engine.Cancel();
        PublishCaptureState();
        if (cancellation is not null) Dispatch(cancellation);
        else if (actionExecutor is { OwnedKeyCount: > 0 } || actionExecutor is { IsActive: true })
            Dispatch(new GestureInvocation(new GestureBinding(GestureId.TopSlideLeft, GestureAction.None),
                Interaction: GestureInteraction.Cancel));
    }

    private void PublishCaptureState()
    {
        activeOriginSnapshot = engine.ActiveOrigin;
        Volatile.Write(ref pointerCaptureRequested,
            PointerCapturePolicy.IsRequested(Settings, Paused, engine.OwnsPointer) ? 1 : 0);
    }

    private void OnFrame(TouchpadDevice device, TouchFrame frame)
    {
        if (Device == device) ProcessFrame(frame);
    }

    internal void ProcessFrame(TouchFrame frame)
    {
        if (!Settings.Enabled || Paused) return;
        if (actionExecutor is { IsActive: false, OwnedKeyCount: > 0 })
        {
            AbortGesture();
            return;
        }
        if (Settings.SuppressWhileTyping && isTyping?.Invoke() == true &&
            engine.Phase is GesturePhase.Idle or GesturePhase.Pointer && frame.ActiveCount > 0)
        {
            engine.Cancel();
            PublishCaptureState();
            return;
        }
        var invocation = engine.Process(frame);
        PublishCaptureState(); // Reserve before begin/update; release before commit/cancel.
        if (invocation is not null) Dispatch(invocation);
    }

    private void Dispatch(GestureInvocation invocation)
    {
        bool succeeded = true;
        string detail = Settings.ObserveOnly ? "observed " : "sent ";
        detail += invocation.Interaction;
        if (!Settings.ObserveOnly)
        {
            try { execute(invocation); }
            catch (InvalidOperationException ex)
            {
                succeeded = false;
                detail = ex.Message;
                engine.Cancel();
                PublishCaptureState();
                if (invocation.Interaction != GestureInteraction.Cancel)
                {
                    try { execute(invocation with { Interaction = GestureInteraction.Cancel }); }
                    catch (InvalidOperationException cleanup) { detail += " " + cleanup.Message; }
                }
            }
        }
        if (succeeded && invocation.Interaction == GestureInteraction.Update) return;
        activities.Enqueue(new GestureActivity(DateTimeOffset.UtcNow, invocation.Binding.Gesture,
            invocation.Binding.Action, invocation.Steps, Settings.ObserveOnly, succeeded, detail));
        while (activities.Count > 32) activities.Dequeue();
        Volatile.Write(ref activitySnapshot, activities.ToArray());
        ActivityVersion++;
    }

    public void Dispose()
    {
        AbortGesture();
        source.FrameReceived -= OnFrame;
        source.DeviceInterrupted -= OnInterrupted;
    }
}
