using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

internal sealed class TrayIcon : IDisposable
{
    internal const string TrayWindowTitle = "TouchPadAdvancedTray";
    internal const uint WmExternalRestore = 0x8000 + 0x171;
    private const uint WmTrayCallback = 0x8000 + 0x170;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const uint MfString = 0x0000, MfChecked = 0x0008, MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002, TpmReturnCmd = 0x0100, TpmBottomAlign = 0x0020;

    private readonly string className = "TouchPadAdvanced.Tray." + Guid.NewGuid().ToString("N");
    private readonly Native.WindowProcedure windowProcedure;
    private readonly Action onOpen;
    private readonly Action onTogglePause;
    private readonly Action onToggleObserve;
    private readonly Action onExit;
    private readonly Func<(bool Paused, bool ObserveOnly)> getState;
    private nint instance, handle, iconHandle;
    private bool classRegistered, added, disposed;
    private string currentTooltip = "";

    internal bool IsInstalled => added && !disposed;

    internal TrayIcon(
        Action onOpen,
        Action onTogglePause,
        Action onToggleObserve,
        Action onExit,
        Func<(bool Paused, bool ObserveOnly)> getState)
    {
        this.onOpen = onOpen;
        this.onTogglePause = onTogglePause;
        this.onToggleObserve = onToggleObserve;
        this.onExit = onExit;
        this.getState = getState;
        windowProcedure = ProcessWindowMessage;
        try
        {
            instance = Native.GetModuleHandle(null);
            var windowClass = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                ClassName = className,
                Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(windowProcedure)
            };
            if (Native.RegisterClassEx(ref windowClass) != 0)
            {
                classRegistered = true;
                handle = Native.CreateWindowEx(0, className, "TouchPadAdvancedTray", 0,
                    0, 0, 0, 0, 0, 0, instance, 0);
            }
            if (handle != 0)
            {
                iconHandle = LoadApplicationIcon();
                var data = CreateData("TouchPad Advanced");
                added = Shell_NotifyIconW(NimAdd, ref data);
                currentTooltip = "TouchPad Advanced";
            }
        }
        catch
        {
            // Non-fatal if shell tray is unavailable in headless/sandbox environments.
        }
    }

    internal void UpdateTooltip(string tooltip)
    {
        if (!added || disposed || tooltip == currentTooltip) return;
        currentTooltip = tooltip;
        var data = CreateData(tooltip);
        Shell_NotifyIconW(NimModify, ref data);
    }

    private NotifyIconData CreateData(string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = handle,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmTrayCallback,
        hIcon = iconHandle,
        szTip = tooltip.Length >= 120 ? tooltip[..120] : tooltip
    };

    private nint ProcessWindowMessage(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmExternalRestore)
        {
            onOpen();
            return 0;
        }
        if (message == WmTrayCallback)
        {
            uint mouseMsg = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (mouseMsg is WmLButtonUp or WmLButtonDblClk)
            {
                onOpen();
                return 0;
            }
            if (mouseMsg is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return 0;
            }
        }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            var (paused, observeOnly) = getState();
            AppendMenuW(menu, MfString, 1, "打开主窗口");
            SetMenuDefaultItem(menu, 1, 0);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString | (paused ? MfChecked : 0), 2, paused ? "继续手势 (已暂停)" : "暂停手势");
            AppendMenuW(menu, MfString | (observeOnly ? MfChecked : 0), 3, "观察模式 (只识别)");
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, 4, "退出 TouchPad Advanced");

            GetCursorPos(out var pt);
            SetForegroundWindow(handle);
            uint cmd = TrackPopupMenu(menu, TpmRightButton | TpmReturnCmd | TpmBottomAlign, pt.X, pt.Y, 0, handle, 0);
            Native.PostMessage(handle, 0, 0, 0);
            switch (cmd)
            {
                case 1: onOpen(); break;
                case 2: onTogglePause(); break;
                case 3: onToggleObserve(); break;
                case 4: onExit(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static nint LoadApplicationIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icoPath))
        {
            nint ico = LoadImageW(0, icoPath, 1, 16, 16, 0x00000010);
            if (ico != 0) return ico;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "TouchPadAdvanced.exe");
        if (File.Exists(exe) && ExtractIconExW(exe, 0, out _, out nint small, 1) > 0 && small != 0)
            return small;
        return LoadIconW(0, new nint(32512)); // IDI_APPLICATION
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (added && handle != 0)
        {
            var data = CreateData("");
            Shell_NotifyIconW(NimDelete, ref data);
            added = false;
        }
        if (handle != 0)
        {
            Native.DestroyWindow(handle);
            handle = 0;
        }
        if (classRegistered)
        {
            Native.UnregisterClass(className, instance);
            classRegistered = false;
        }
        GC.KeepAlive(windowProcedure);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointInt32 { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointInt32 lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
