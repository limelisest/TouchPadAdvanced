using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace TouchPadAdvanced;

// The Windows cursor is shared by all mice. Capture explicitly reserves this shared cursor;
// it does not claim per-device filtering that WH_MOUSE_LL cannot provide.
public sealed class PointerCaptureController : IDisposable
{
    private const int HotkeyId = 0x5450;
    private readonly RawInputSource source;
    private readonly GestureRuntime runtime;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private Exception? startupError;
    private int threadId, installed, disposed, forceRelease, emergencyHotkeyAvailable;
    private long suppressed, synchronizationTimeouts;
    private bool synchronizing;
    public bool IsInstalled => Volatile.Read(ref installed) != 0;
    public bool EmergencyHotkeyAvailable => Volatile.Read(ref emergencyHotkeyAvailable) != 0;
    public long SuppressedEvents => Interlocked.Read(ref suppressed);
    public long SynchronizationTimeouts => Interlocked.Read(ref synchronizationTimeouts);
    public bool IsCapturing => IsInstalled && runtime.PointerCaptureRequested && Volatile.Read(ref forceRelease) == 0;

    public PointerCaptureController(RawInputSource source, GestureRuntime runtime)
    {
        this.source = source;
        this.runtime = runtime;
        thread = new Thread(Run) { IsBackground = true, Name = "TouchPadAdvanced.PointerCapture" };
        source.CapturePauseRequested += TogglePause;
        thread.Start();
        ready.Wait();
        if (startupError is not null)
        {
            source.CapturePauseRequested -= TogglePause;
            ready.Dispose();
            ExceptionDispatchInfo.Capture(startupError).Throw();
        }
    }

    private void Run()
    {
        bool hotkey = false;
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0); // Establish the thread's message queue.
            threadId = unchecked((int)Native.GetCurrentThreadId());
            using var hook = new MouseInputHook(OnMouse);
            hotkey = Native.RegisterHotKey(0, HotkeyId, 0x4003, 0x77);
            Volatile.Write(ref emergencyHotkeyAvailable, hotkey ? 1 : 0); // Ctrl+Alt+F8, no repeat.
            Volatile.Write(ref installed, 1);
            ready.Set();
            while (true)
            {
                int result = Native.GetMessage(out var message, 0, 0, 0);
                if (result < 0) throw new Win32Exception();
                if (result == 0) break;
                if (message.Message == 0x0312 && (int)message.WParam == HotkeyId)
                {
                    Volatile.Write(ref forceRelease, 1);
                    Native.PostMessage(source.Handle, Native.WmCapturePause, 0, 0);
                }
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            startupError = ex;
            Volatile.Write(ref forceRelease, 1);
            Native.PostMessage(source.Handle, Native.WmCapturePause, 1, 0);
        }
        finally
        {
            Volatile.Write(ref installed, 0);
            Volatile.Write(ref forceRelease, 1);
            Volatile.Write(ref emergencyHotkeyAvailable, 0);
            if (Volatile.Read(ref disposed) == 0) Native.PostMessage(source.Handle, Native.WmCapturePause, 1, 0);
            if (hotkey) Native.UnregisterHotKey(0, HotkeyId);
            ready.Set();
        }
    }

    private bool OnMouse(MouseObservation mouse)
    {
        if (!PointerCapturePolicy.IsPhysicalMotion(mouse)) return false;
        if (!synchronizing)
        {
            synchronizing = true;
            try
            {
                // Raw Input and cursor messages have different delivery paths. Drain already queued
                // contact reports before deciding, so touchdown can reserve the very first movement.
                // Do not use SMTO_BLOCK: injected side-key callbacks must be allowed to re-enter.
                if (Native.SendMessageTimeout(source.Handle, Native.WmDrainInput, 0, 0, 2, 15, out _) == 0)
                {
                    Interlocked.Increment(ref synchronizationTimeouts);
                    return false;
                }
            }
            finally { synchronizing = false; }
        }
        bool capture = PointerCapturePolicy.ShouldSuppress(mouse, IsCapturing);
        if (capture) Interlocked.Increment(ref suppressed);
        return capture;
    }

    private void TogglePause(bool force)
    {
        runtime.SetPaused(force || !runtime.Paused);
        Volatile.Write(ref forceRelease, 0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Volatile.Write(ref forceRelease, 1);
        source.CapturePauseRequested -= TogglePause;
        if (thread.IsAlive)
        {
            Native.PostThreadMessage(unchecked((uint)threadId), 0x0012, 0, 0);
            thread.Join();
        }
        ready.Dispose();
    }
}

internal static class PointerCapturePolicy
{
    internal static bool IsRequested(GestureSettings settings, bool paused, bool ownsPointer) =>
        settings.Enabled && settings.CapturePointer && !settings.ObserveOnly && !paused && ownsPointer;

    internal static bool IsPhysicalMotion(MouseObservation mouse) =>
        mouse.Message == 0x0200 && (mouse.Flags & 3) == 0 && mouse.ExtraInfo != (ulong)GestureActions.InputTag;

    internal static bool ShouldSuppress(MouseObservation mouse, bool ownsPointer) =>
        ownsPointer && IsPhysicalMotion(mouse);
}
