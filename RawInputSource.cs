using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

public sealed record RawSample(DateTimeOffset ReceivedAt, string Hex);

public sealed class TouchpadDevice : IDisposable
{
    internal nint Handle { get; }
    public string Path { get; }
    public string Label { get; }
    private volatile bool connected = true;
    public bool Connected { get => connected; internal set => connected = value; }
    public long ReportCount { get; private set; }
    public long FrameCount { get; private set; }
    public long ErrorCount { get; private set; }
    public int MaxActiveContacts { get; private set; }
    public long IncompleteFrames => assembler.IncompleteFrames;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastReceivedAt { get; private set; }
    private TouchFrame? frameSnapshot;
    public TouchFrame? Frame => Volatile.Read(ref frameSnapshot);
    public HidField[] Fields => descriptor?.Fields ?? [];
    public bool CanDecodeContacts => descriptor?.CanDecodeContacts == true;
    public int InputReportLength => descriptor?.Caps.InputLength ?? 0;
    public double AspectRatio
    {
        get
        {
            var x = Fields.FirstOrDefault(f => f.Page == 1 && f.Usage == 0x30 && f.Max > f.Min);
            var y = Fields.FirstOrDefault(f => f.Page == 1 && f.Usage == 0x31 && f.Max > f.Min);
            if (x is null || y is null) return 1.5;
            double ratio = (x.Max - (double)x.Min) / (y.Max - (double)y.Min);
            return double.IsFinite(ratio) ? Math.Clamp(ratio, 1.0, 2.2) : 1.5;
        }
    }
    private readonly Queue<RawSample> reports = new();
    private RawSample[] reportSnapshot = [];
    public IReadOnlyList<RawSample> RecentReports => Volatile.Read(ref reportSnapshot);
    private readonly FrameAssembler assembler = new();
    private readonly HidDescriptor? descriptor;

    internal TouchpadDevice(nint handle, Native.DeviceInfo info, string path)
    {
        Handle = handle;
        Path = path;
        Label = $"VID {info.Vendor:X4} / PID {info.Product:X4} · {path.Split('#').ElementAtOrDefault(1) ?? path}";
        try { descriptor = new HidDescriptor(handle); }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            LastError = ex.Message;
            ErrorCount++;
        }
    }

    internal TouchFrame? Accept(byte[] report)
    {
        var now = DateTimeOffset.UtcNow;
        LastReceivedAt = now;
        ReportCount++;
        reports.Enqueue(new RawSample(now, Convert.ToHexString(report)));
        while (reports.Count > 128) reports.Dequeue();
        Volatile.Write(ref reportSnapshot, reports.ToArray());
        if (descriptor is null) return null;
        try
        {
            var packet = descriptor.Parse(report);
            if (packet is null) return null;
            var frame = assembler.Push(packet, now);
            if (frame is null) return null;
            Volatile.Write(ref frameSnapshot, frame);
            FrameCount++;
            MaxActiveContacts = Math.Max(MaxActiveContacts, frame.ActiveCount);
            LastError = null;
            return frame;
        }
        catch (InvalidDataException ex)
        {
            LastError = ex.Message;
            ErrorCount++;
            return null;
        }
    }

    public void Dispose() => descriptor?.Dispose();
}

// Callbacks are delivered on the owning thread; the caller supplies its message loop. Rendering is throttled separately;
// every WM_INPUT report is still processed, including all dwCount reports in RAWHID.
public sealed class RawInputSource : IDisposable
{
    internal event Action<TouchpadDevice, TouchFrame>? FrameReceived;
    internal event Action<TouchpadDevice>? DeviceInterrupted;
    internal event Action<bool>? CapturePauseRequested;
    private bool processingInput;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly Dictionary<nint, TouchpadDevice> connected = [];
    private readonly List<TouchpadDevice> devices = [];
    private TouchpadDevice[] deviceSnapshot = [];
    public IReadOnlyList<TouchpadDevice> Devices => Volatile.Read(ref deviceSnapshot);
    private readonly Queue<string> errors = new();
    private string[] errorSnapshot = [];
    public IReadOnlyCollection<string> Errors => Volatile.Read(ref errorSnapshot);
    private nint inputBuffer;
    private uint inputCapacity;
    private bool registered;
    private readonly string className = "TouchPadAdvanced.RawInput." + Guid.NewGuid().ToString("N");
    private readonly Native.WindowProcedure windowProcedure;
    private nint instance;
    private bool classRegistered;
    internal nint Handle { get; private set; }
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Native.InputHeader>();

    public RawInputSource()
    {
        windowProcedure = ProcessWindowMessage;
        try
        {
            instance = Native.GetModuleHandle(null);
            var windowClass = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                ClassName = className, Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(windowProcedure)
            };
            if (Native.RegisterClassEx(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx");
            classRegistered = true;
            Handle = Native.CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new nint(-3), 0, instance, 0);
            if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx");

            var registration = new Native.Registration
            {
                UsagePage = 0x0D, Usage = 0x05,
                Flags = Native.InputSink | Native.DeviceNotify, Target = Handle
            };
            if (!Native.RegisterRawInputDevices([registration], 1, (uint)Marshal.SizeOf<Native.Registration>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices");
            registered = true;
            RefreshDevices();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RefreshDevices()
    {
        if (!IsOwnerThread) { Invoke(RefreshDevices); return; }
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<Native.DeviceList>();
        Native.CheckWin32(Native.GetRawInputDeviceList(null, ref count, itemSize), "枚举输入设备");
        if (count == 0)
        {
            foreach (var handle in connected.Keys.ToArray()) RemoveDevice(handle);
            return;
        }
        var list = new Native.DeviceList[count];
        uint found = Native.GetRawInputDeviceList(list, ref count, itemSize);
        Native.CheckWin32(found, "枚举输入设备");
        var handles = new HashSet<nint>();
        foreach (var item in list.Take((int)found))
        {
            if (item.Type != 2) continue;
            handles.Add(item.Handle);
            AddDevice(item.Handle);
        }
        foreach (var handle in connected.Keys.Where(h => !handles.Contains(h)).ToArray())
            RemoveDevice(handle);
    }

    private void AddDevice(nint handle)
    {
        if (connected.ContainsKey(handle)) return;
        nint infoBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.DeviceInfo>());
        try
        {
            uint size = (uint)Marshal.SizeOf<Native.DeviceInfo>();
            Marshal.WriteInt32(infoBuffer, (int)size);
            Native.CheckWin32(Native.GetDeviceInfo(handle, Native.DeviceInfoCommand, infoBuffer, ref size),
                "读取设备类型");
            var info = Marshal.PtrToStructure<Native.DeviceInfo>(infoBuffer);
            if (info.Type != 2 || info.UsagePage != 0x0D || info.Usage != 0x05) return;

            uint chars = 0;
            Native.CheckWin32(Native.GetDeviceInfo(handle, Native.DeviceName, 0, ref chars), "读取设备名称长度");
            nint name = Marshal.AllocHGlobal(checked(((int)chars + 1) * 2));
            try
            {
                Native.CheckWin32(Native.GetDeviceInfo(handle, Native.DeviceName, name, ref chars), "读取设备名称");
                var device = new TouchpadDevice(handle, info, Marshal.PtrToStringUni(name) ?? "");
                connected.Add(handle, device);
                devices.Add(device);
                Volatile.Write(ref deviceSnapshot, devices.ToArray());
            }
            finally { Marshal.FreeHGlobal(name); }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            ReportError(ex.Message);
        }
        finally { Marshal.FreeHGlobal(infoBuffer); }
    }

    private void RemoveDevice(nint handle)
    {
        if (!connected.Remove(handle, out var device)) return;
        device.Connected = false;
        DeviceInterrupted?.Invoke(device);
        device.Dispose();
    }

    private void ReadInput(nint rawInput)
    {
        uint size = 0;
        Native.CheckWin32(Native.GetRawInputData(rawInput, Native.RidInput, 0, ref size, HeaderSize),
            "读取 Raw Input 长度");
        if (size > inputCapacity)
        {
            inputBuffer = Marshal.ReAllocHGlobal(inputBuffer, checked((nint)size));
            inputCapacity = size;
        }
        uint read = Native.GetRawInputData(rawInput, Native.RidInput, inputBuffer, ref size, HeaderSize);
        Native.CheckWin32(read, "读取 Raw Input");
        if (read < HeaderSize + 8) throw new InvalidDataException("Raw Input 长度不足。");
        var header = Marshal.PtrToStructure<Native.InputHeader>(inputBuffer);
        if (header.Type != 2) return;
        if (!connected.TryGetValue(header.Device, out var device))
        {
            AddDevice(header.Device);
            if (!connected.TryGetValue(header.Device, out device)) return;
        }
        nint hid = inputBuffer + (int)HeaderSize;
        uint reportSize = unchecked((uint)Marshal.ReadInt32(hid));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(hid, 4));
        if (reportSize == 0 || (ulong)reportSize * reportCount > read - HeaderSize - 8)
            throw new InvalidDataException("RAWHID 长度与报文数量不匹配。");
        for (uint i = 0; i < reportCount; i++)
        {
            var report = new byte[reportSize];
            Marshal.Copy(hid + 8 + checked((int)(i * reportSize)), report, 0, report.Length);
            long errors = device.ErrorCount, incomplete = device.IncompleteFrames;
            var frame = device.Accept(report);
            if (device.ErrorCount != errors || device.IncompleteFrames != incomplete)
                DeviceInterrupted?.Invoke(device);
            if (frame is not null) FrameReceived?.Invoke(device, frame);
        }
    }

    private void ReportError(string error)
    {
        errors.Enqueue($"{DateTimeOffset.UtcNow:O} {error}");
        while (errors.Count > 32) errors.Dequeue();
        Volatile.Write(ref errorSnapshot, errors.ToArray());
    }

    internal bool IsOwnerThread => Environment.CurrentManagedThreadId == ownerThread;

    internal void Invoke(Action action)
    {
        if (IsOwnerThread) { action(); return; }
        commands.Enqueue(action);
        if (!Native.PostMessage(Handle, Native.WmInvoke, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PostMessage(input command)");
    }
    private void DrainPendingInput()
    {
        if (processingInput) return;
        processingInput = true;
        try
        {
            for (int i = 0; i < 64 && Native.PeekMessage(out var message, Handle,
                (uint)Native.WmInput, (uint)Native.WmInput, 1); i++)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        finally { processingInput = false; }
    }
    private nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == Native.WmInvoke)
            {
                while (commands.TryDequeue(out var command)) command();
            }
            else if (message == Native.WmDrainInput) DrainPendingInput();
            else if (message == Native.WmCapturePause) CapturePauseRequested?.Invoke(wParam != 0);
            else if (message == Native.WmInput)
            {
                // SendInput can pump sent messages. A recursive drain must not overwrite
                // the shared HID buffer or execute another gesture during this callback.
                bool wasProcessing = processingInput;
                processingInput = true;
                try { ReadInput(lParam); }
                finally { processingInput = wasProcessing; }
            }
            else if (message == Native.WmDeviceChange)
            {
                if (wParam == 1) AddDevice(lParam);
                else if (wParam == 2) RemoveDevice(lParam);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or OverflowException)
        {
            ReportError(ex.Message);
            foreach (var device in connected.Values) DeviceInterrupted?.Invoke(device);
        }
        // Foreground WM_INPUT must be cleaned up after reading its data.
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (registered)
        {
            Native.RegisterRawInputDevices(
                [new Native.Registration { UsagePage = 0x0D, Usage = 0x05, Flags = Native.Remove }],
                1, (uint)Marshal.SizeOf<Native.Registration>());
            registered = false;
        }
        foreach (var device in Devices) device.Dispose();
        if (inputBuffer != 0)
        {
            Marshal.FreeHGlobal(inputBuffer);
            inputBuffer = 0;
            inputCapacity = 0;
        }
        if (Handle != 0)
        {
            Native.DestroyWindow(Handle);
            Handle = 0;
        }
        if (classRegistered)
        {
            Native.UnregisterClass(className, instance);
            classRegistered = false;
        }
        GC.KeepAlive(windowProcedure);
    }
}
