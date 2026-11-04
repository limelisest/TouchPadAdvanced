using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace TouchPadAdvanced;

public partial class App : Application
{
    private Mutex? instance;
    private MainWindow? window;
    private DispatcherTimer? smokeTimer;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            string directory = System.IO.Path.GetDirectoryName(GestureSettingsStore.Path)!;
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "startup-error.log"), e.Exception.ToString());
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        bool startupLaunch = commandLine.Contains("--startup");
        int smokeIndex = Array.IndexOf(commandLine, "--smoke");
        bool smoke = smokeIndex >= 0 && smokeIndex + 1 < commandLine.Length;
        string mutexName = smoke ? @"Local\TouchPadAdvanced.Edges.Smoke" : @"Local\TouchPadAdvanced.Edges";
        instance = new Mutex(true, mutexName, out bool first);
        if (!first)
        {
            if (!startupLaunch && !smoke)
            {
                nint tray = FindWindow(null, TrayIcon.TrayWindowTitle);
                if (tray != 0) PostMessage(tray, TrayIcon.WmExternalRestore, 0, 0);
                nint existing = FindWindow(null, "TouchPad Advanced");
                if (existing != 0)
                {
                    ShowWindow(existing, 5);
                    ShowWindow(existing, 9);
                    SetForegroundWindow(existing);
                }
            }
            instance.Dispose();
            Exit();
            return;
        }
        window = new MainWindow(smoke);
        window.Closed += (_, _) => { instance.ReleaseMutex(); instance.Dispose(); };
        if (smoke)
        {
            string path = commandLine[smokeIndex + 1];
            smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            smokeTimer.Tick += async (_, _) =>
            {
                smokeTimer.Stop();
                try
                {
                    var shortcutInput = await window.ExerciseShortcutInputAsync();
                    System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(window.SmokeState(shortcutInput),
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    await window.CaptureEditorAsync(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                }
                finally { window.ExitApplication(); }
            };
        }
        window.Activate();
        if (startupLaunch && !smoke)
            window.EnterStartupMode();
        smokeTimer?.Start();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
