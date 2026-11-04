using Microsoft.Win32;

namespace TouchPadAdvanced;

// Per-user Windows logon registration. The value launches the apphost directly.
public sealed class StartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "TouchPadAdvanced";
    private readonly string registryPath;
    public string ExecutablePath { get; }
    public string Command => $"\"{ExecutablePath}\" --startup";

    public StartupRegistration(string executablePath) : this(executablePath, RunKey) { }

    internal StartupRegistration(string executablePath, string registryPath)
    {
        if (!Path.IsPathFullyQualified(executablePath) ||
            !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("自启程序必须是完整的 EXE 路径。", nameof(executablePath));
        ExecutablePath = Path.GetFullPath(executablePath);
        this.registryPath = registryPath;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath);
            return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command &&
                key.GetValueKind(ValueName) == RegistryValueKind.String &&
                string.Equals(command, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("未找到自启程序。", ExecutablePath);
            if (Command.Length > 260) throw new IOException("程序路径过长，无法注册 Windows 自启。");
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        if (IsEnabled != enabled) throw new IOException("Windows 自启设置未能保存。");
    }
}
