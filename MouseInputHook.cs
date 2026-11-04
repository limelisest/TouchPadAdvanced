using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

internal readonly record struct MouseObservation(uint Message, int X, int Y, uint Flags,
    ulong ExtraInfo, uint Time, uint DeviceType, uint Origin, bool SourceAvailable, long Timestamp);

internal sealed class MouseInputHook : IDisposable
{
    private readonly Native.MouseHookProcedure procedure;
    private readonly Func<MouseObservation, bool> suppress;
    private nint handle;

    internal MouseInputHook(Func<MouseObservation, bool> suppress)
    {
        this.suppress = suppress;
        procedure = OnMouse;
        handle = Native.SetWindowsHookEx(14, procedure, Native.GetModuleHandle(null), 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL)");
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var mouse = Marshal.PtrToStructure<Native.LowLevelMouseInput>(data);
            bool available = Native.GetCurrentInputMessageSource(out var source);
            var observation = new MouseObservation((uint)message, mouse.X, mouse.Y, mouse.Flags,
                (ulong)mouse.ExtraInfo, mouse.Time, source.DeviceType, source.Origin, available,
                Stopwatch.GetTimestamp());
            if (suppress(observation)) return 1;
        }
        return Native.CallNextHookEx(handle, code, message, data);
    }

    public void Dispose()
    {
        if (handle == 0) return;
        Native.UnhookWindowsHookEx(handle);
        handle = 0;
        GC.KeepAlive(procedure);
    }
}

