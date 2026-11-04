using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace TouchPadAdvanced;

// Native input and gesture state live on this message thread. UI reads immutable snapshots
// and posts configuration commands, so layout, dialogs and disk writes cannot stall input.
public sealed class GestureInputHost : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly GestureSettings initialSettings;
    private readonly TypingActivityGuard typingGuard = new();
    private Exception? startupError;
    private int threadId, running, disposed;
    public RawInputSource Source { get; private set; } = null!;
    public GestureRuntime Runtime { get; private set; } = null!;
    public PointerCaptureController Capture { get; private set; } = null!;
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public GestureInputHost(GestureSettings settings)
    {
        settings.Validate();
        initialSettings = settings;
        thread = new Thread(Run) { IsBackground = true, Name = "TouchPadAdvanced.Input" };
        thread.Start();
        ready.Wait();
        if (startupError is not null)
        {
            thread.Join();
            ready.Dispose();
            ExceptionDispatchInfo.Capture(startupError).Throw();
        }
    }

    private void Run()
    {
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0);
            threadId = unchecked((int)Native.GetCurrentThreadId());
            Source = new RawInputSource();
            Runtime = new GestureRuntime(Source, initialSettings, isTyping: () => typingGuard.PollIsTyping());
            Capture = new PointerCaptureController(Source, Runtime);
            Volatile.Write(ref running, 1);
            ready.Set();
            while (true)
            {
                int result = Native.GetMessage(out var message, 0, 0, 0);
                if (result < 0) throw new Win32Exception();
                if (result == 0) break;
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        catch (Exception ex) { startupError = ex; }
        finally
        {
            Volatile.Write(ref running, 0);
            ready.Set();
            Runtime?.SetPaused(true);
            Capture?.Dispose();
            Runtime?.Dispose();
            Source?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (thread.IsAlive)
        {
            Native.PostThreadMessage(unchecked((uint)threadId), 0x0012, 0, 0);
            thread.Join();
        }
        ready.Dispose();
    }
}
