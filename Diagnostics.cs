using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TouchPadAdvanced;

public static class Diagnostics
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Save(RawInputSource source, string path, GestureRuntime? runtime = null, PointerCaptureController? capture = null)
    {
        var snapshot = new
        {
            schemaVersion = 2,
            settings = runtime?.Settings,
            pointerCapture = capture is null ? null : new
            {
                capture.IsInstalled, capture.IsCapturing, capture.SuppressedEvents,
                capture.SynchronizationTimeouts, capture.EmergencyHotkeyAvailable
            },
            gestureActivities = runtime?.Activities.ToArray(),
            capturedAt = DateTimeOffset.UtcNow,
            windows = Environment.OSVersion.VersionString,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            devices = source.Devices.Select(d => new
            {
                d.Label, d.Path, d.Connected, d.CanDecodeContacts, d.InputReportLength, d.Fields,
                d.ReportCount, d.FrameCount, d.MaxActiveContacts, d.IncompleteFrames,
                d.ErrorCount, d.LastError, d.LastReceivedAt, d.Frame,
                recentReports = d.RecentReports.ToArray()
            }).ToArray(),
            errors = source.Errors.ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions), new UTF8Encoding(false));
    }
}
