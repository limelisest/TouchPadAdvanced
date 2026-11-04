using Microsoft.Win32;

namespace TouchPadAdvanced;

internal static class StartupTests
{
    internal static readonly (string, Action)[] Cases =
    [
        ("Startup registers a quoted EXE directly and preserves other values", Lifecycle),
        ("Startup reads registry state and rejects unrelated commands or value types", RegistryState),
        ("Startup rejects missing and ambiguous executable paths without registering", InvalidPaths),
        ("Startup enforces the Windows Run command length limit", CommandLimit)
    ];

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static void InSandbox(Action<string, string> test)
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TouchPadAdvanced-startup-" + id));
        string registry = @"Software\TouchPadAdvanced.Tests\Startup\" + id;
        string executable = Path.Combine(root, "folder with spaces", "TouchPadAdvanced.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, [0x4d, 0x5a]); // Existence fixture, never executed.
        try { test(executable, registry); }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registry, throwOnMissingSubKey: false);
            Check(string.Equals(Path.GetDirectoryName(root), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase), "Cleanup stays within the test temporary directory.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Lifecycle() => InSandbox((exe, path) =>
    {
        var startup = new StartupRegistration(exe, path);
        Check(!startup.IsEnabled, "New registration is off.");
        using (var key = Registry.CurrentUser.CreateSubKey(path))
            key.SetValue("OtherApplication", 37, RegistryValueKind.DWord);
        startup.SetEnabled(true);
        startup.SetEnabled(true);
        using (var key = Registry.CurrentUser.OpenSubKey(path)!)
        {
            Check((string)key.GetValue(StartupRegistration.ValueName)! == $"\"{exe}\" --startup",
                "Run launches the quoted EXE with no shell or script.");
            Check(key.GetValueKind(StartupRegistration.ValueName) == RegistryValueKind.String, "Use REG_SZ.");
            Check((int)key.GetValue("OtherApplication")! == 37, "Other values are unchanged.");
        }
        Check(new StartupRegistration(exe, path).IsEnabled, "A new instance reads the saved setting.");
        startup.SetEnabled(false);
        startup.SetEnabled(false);
        using (var key = Registry.CurrentUser.OpenSubKey(path)!)
            Check(key.GetValue(StartupRegistration.ValueName) is null &&
                (int)key.GetValue("OtherApplication")! == 37, "Only this app's value is removed.");
        Check(!startup.IsEnabled, "Disabled state reads back.");
    });

    private static void RegistryState() => InSandbox((exe, path) =>
    {
        var startup = new StartupRegistration(exe, path);
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(StartupRegistration.ValueName, startup.Command, RegistryValueKind.ExpandString);
        Check(!startup.IsEnabled, "An expanding value is not our registration.");
        key.SetValue(StartupRegistration.ValueName, startup.Command + " --unexpected", RegistryValueKind.String);
        Check(!startup.IsEnabled, "Unexpected arguments do not count as enabled.");
        key.SetValue(StartupRegistration.ValueName, startup.Command.ToUpperInvariant(), RegistryValueKind.String);
        Check(startup.IsEnabled, "Windows executable paths are case insensitive.");
        key.DeleteValue(StartupRegistration.ValueName);
        Check(!startup.IsEnabled, "External changes are reflected immediately.");
    });

    private static void InvalidPaths() => InSandbox((exe, path) =>
    {
        foreach (var invalid in new[] { "relative.exe", exe + " --flag", exe + "\n", exe.Replace(".exe", ".cmd") })
        {
            bool rejected = false;
            try { _ = new StartupRegistration(invalid, path); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Reject non-EXE or ambiguous paths.");
        }
        var missing = new StartupRegistration(Path.Combine(Path.GetDirectoryName(exe)!, "missing.exe"), path);
        bool missingRejected = false;
        try { missing.SetEnabled(true); }
        catch (FileNotFoundException) { missingRejected = true; }
        using var key = Registry.CurrentUser.OpenSubKey(path);
        Check(missingRejected && key is null, "A missing executable never creates a startup registration.");
    });

    private static void CommandLimit() => InSandbox((exe, path) =>
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(exe))!;
        string folder = Path.Combine(root, new string('a', 230 - root.Length));
        Directory.CreateDirectory(folder);
        string longExe = Path.Combine(folder, "TouchPadAdvanced.exe");
        File.WriteAllBytes(longExe, [0x4d, 0x5a]);
        var startup = new StartupRegistration(longExe, path);
        Check(startup.Command.Length > 260, "Fixture exceeds the documented Run limit.");
        bool rejected = false;
        try { startup.SetEnabled(true); }
        catch (IOException) { rejected = true; }
        using var key = Registry.CurrentUser.OpenSubKey(path);
        Check(rejected && key is null, "Oversized commands are rejected before registration.");
    });
}
