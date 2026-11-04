using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

internal static class Native
{
    internal const uint Error = uint.MaxValue;
    internal const int HidSuccess = 0x00110000;
    internal const int WmInput = 0x00FF, WmDeviceChange = 0x00FE;
    internal const uint InputSink = 0x100, DeviceNotify = 0x2000, Remove = 1;
    internal const uint RidInput = 0x10000003, DeviceName = 0x20000007,
        DeviceInfoCommand = 0x2000000B, PreparsedData = 0x20000005;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceList { internal nint Handle; internal uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Registration
    {
        internal ushort UsagePage, Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputHeader
    {
        internal uint Type, Size;
        internal nint Device, WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct DeviceInfo
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(4)] internal uint Type;
        [FieldOffset(8)] internal uint Vendor;
        [FieldOffset(12)] internal uint Product;
        [FieldOffset(20)] internal ushort UsagePage;
        [FieldOffset(22)] internal ushort Usage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Caps
    {
        [FieldOffset(0)] internal ushort Usage;
        [FieldOffset(2)] internal ushort UsagePage;
        [FieldOffset(4)] internal ushort InputLength;
        [FieldOffset(44)] internal ushort LinkNodes;
        [FieldOffset(46)] internal ushort InputButtons;
        [FieldOffset(48)] internal ushort InputValues;
    }

    // HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS share these header and union offsets.
    // Value-specific members are read only for value capabilities.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    internal struct Capability
    {
        [FieldOffset(0)] internal ushort UsagePage;
        [FieldOffset(2)] internal byte ReportId;
        [FieldOffset(3)] internal byte IsAlias;
        [FieldOffset(6)] internal ushort LinkCollection;
        [FieldOffset(12)] internal byte IsRange;
        [FieldOffset(18)] internal ushort BitSize;
        [FieldOffset(20)] internal ushort ReportCount;
        [FieldOffset(40)] internal int LogicalMin;
        [FieldOffset(44)] internal int LogicalMax;
        [FieldOffset(48)] internal int PhysicalMin;
        [FieldOffset(52)] internal int PhysicalMax;
        [FieldOffset(56)] internal ushort UsageMin;
        [FieldOffset(58)] internal ushort UsageMax;
        [FieldOffset(68)] internal ushort DataIndexMin;
        [FieldOffset(70)] internal ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LinkNode
    {
        internal ushort Usage, UsagePage, Parent, ChildCount, NextSibling, FirstChild;
        internal uint Flags;
        internal nint UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidData
    {
        internal ushort DataIndex, Reserved;
        internal uint RawValue;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList([Out] DeviceList[]? list, ref uint count, uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static extern uint GetDeviceInfo(nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(nint preparsed, out Caps caps);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(int type, [Out] Capability[] caps, ref ushort count, nint preparsed);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetButtonCaps(int type, [Out] Capability[] caps, ref ushort count, nint preparsed);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetLinkCollectionNodes([Out] LinkNode[] nodes, ref uint count, nint preparsed);

    [DllImport("hid.dll")]
    internal static extern uint HidP_MaxDataListLength(int type, nint preparsed);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetData(int type, [Out] HidData[] data, ref uint count,
        nint preparsed, byte[] report, uint length);


    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);


    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMessage(out WindowMessage message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref WindowMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DispatchMessage(ref WindowMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint window, nuint id);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LowLevelMouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InputMessageSource
    {
        internal uint DeviceType, Origin;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint MouseHookProcedure(int code, nint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int type, MouseHookProcedure procedure, nint module, uint thread);

    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCurrentInputMessageSource(out InputMessageSource source);

    internal const uint WmDrainInput = 0x8000 + 0x161;
    internal const uint WmInvoke = 0x8000 + 0x163;
    internal const uint WmCapturePause = 0x8000 + 0x162;

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out WindowMessage message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint milliseconds, out nuint result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(nint device, uint ioControlCode,
        byte[]? inBuffer, uint inBufferSize, byte[]? outBuffer, uint outBufferSize,
        out uint bytesReturned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    internal static void CheckHid(int status, string operation)
    {
        if (status != HidSuccess)
            throw new InvalidDataException($"{operation}: HID status 0x{status:X8}");
    }

    internal static void CheckWin32(uint result, string operation)
    {
        if (result == Error) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
}
