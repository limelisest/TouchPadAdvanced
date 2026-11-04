using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

internal sealed record TestResult(string Name, bool Passed, string? Error);
internal sealed record TestRun(bool Passed, TestResult[] Results);

internal static class SelfTests
{
    internal static TestRun Run()
    {
        var tests = new (string, Action)[]
        {
            ("Windows x64 ABI layouts", Layouts),
            ("Parallel frame ignores unused contact slots", Parallel),
            ("Hybrid frame waits for every contact", Hybrid),
            ("Lifted contacts count toward frame but not active fingers", Lift),
            ("New scan discards an incomplete hybrid frame", DroppedFrame),
            ("Scan-time wrap and reused contact ID", Wrap),
            ("Duplicate IDs are rejected", Duplicate),
            ("HID decoder handles absent OFF buttons, zero IDs and signed coordinates", Decoder)
        };
        var results = tests.Concat(EdgeGestureTests.Cases).Concat(CaptureTests.Cases).Concat(StartupTests.Cases).Concat(CompositeGestureTests.Cases).Concat(SelectionTests.Cases).Concat(ShortcutEditingTests.Cases).Select(test =>
        {
            try { test.Item2(); return new TestResult(test.Item1, true, null); }
            catch (Exception ex) { return new TestResult(test.Item1, false, ex.Message); }
        }).ToArray();
        return new TestRun(results.All(r => r.Passed), results);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static Contact C(int id, bool tip = true) =>
        new(id, 100, 200, tip, true, null, null, null, new Axis(0, 1000), new Axis(0, 1000));

    private static void Layouts()
    {
        Check(IntPtr.Size == 8, "This build targets x64.");
        Check(Marshal.SizeOf<Native.InputHeader>() == 24, "RAWINPUTHEADER");
        Check(Marshal.SizeOf<Native.Registration>() == 16, "RAWINPUTDEVICE");
        Check(Marshal.SizeOf<Native.DeviceList>() == 16, "RAWINPUTDEVICELIST");
        Check(Marshal.SizeOf<Native.Caps>() == 64, "HIDP_CAPS");
        Check(Marshal.SizeOf<Native.Capability>() == 72, "HIDP capability");
        Check(Marshal.SizeOf<Native.LinkNode>() == 24, "HIDP_LINK_COLLECTION_NODE");
        Check(Marshal.SizeOf<Native.HidData>() == 8, "HIDP_DATA");
    }

    private static void Parallel()
    {
        var a = new FrameAssembler();
        var f = a.Push(new(2, 100, 1, [C(0), C(9), C(0)]), Now);
        Check(f is { ActiveCount: 2, Buttons: 1 } && f.Contacts[1].Id == 9, "Must ignore unused slot.");
    }

    private static void Hybrid()
    {
        var a = new FrameAssembler();
        Check(a.Push(new(5, 100, 1, [C(0), C(1)]), Now) is null, "First packet is partial.");
        Check(a.Push(new(0, 100, 0, [C(2), C(3)]), Now) is null, "Second packet is partial.");
        var f = a.Push(new(0, 100, 0, [C(4), C(0)]), Now);
        Check(f is { ActiveCount: 5, Buttons: 1 } && a.IncompleteFrames == 0, "Assemble five contacts.");
    }

    private static void Lift()
    {
        var a = new FrameAssembler();
        var f = a.Push(new(2, 10, 0, [C(0, false), C(1)]), Now);
        Check(f is { ActiveCount: 1 } && f.Contacts.Length == 2, "Release is a reported contact.");
        f = a.Push(new(1, 11, 0, [C(1, false)]), Now);
        Check(f is { ActiveCount: 0 }, "Last tip-up must clear active fingers.");
        f = a.Push(new(0, 12, 0, [C(0)]), Now);
        Check(f is { ActiveCount: 0 } && f.Contacts.Length == 0, "Empty count ignores stale slots.");
    }

    private static void DroppedFrame()
    {
        var a = new FrameAssembler();
        a.Push(new(3, 1, 0, [C(0)]), Now);
        var f = a.Push(new(1, 2, 0, [C(7)]), Now);
        Check(f is { ActiveCount: 1 } && f.Contacts[0].Id == 7 && a.IncompleteFrames == 1,
            "New start must discard incomplete contacts.");
        a.Push(new(3, 3, 0, [C(0)]), Now);
        f = a.Push(new(0, 4, 0, [C(1)]), Now);
        Check(f is { ActiveCount: 0 } && a.IncompleteFrames == 2, "Different scan is not a continuation.");
    }

    private static void Wrap()
    {
        var a = new FrameAssembler();
        a.Push(new(1, 65535, 0, [C(0, false)]), Now);
        var f = a.Push(new(2, 0, 0, [C(0)]), Now);
        Check(f is null, "Wrap must accept a new hybrid start.");
        f = a.Push(new(0, 0, 0, [C(5)]), Now);
        Check(f is { ActiveCount: 2 } && f.Contacts[0].Id == 0, "Zero scan and reused ID are valid.");
    }

    private static void Duplicate()
    {
        var a = new FrameAssembler();
        try { a.Push(new(2, 1, 0, [C(0), C(0)]), Now); }
        catch (InvalidDataException)
        {
            Check(a.IncompleteFrames == 1, "Reject the whole frame.");
            return;
        }
        throw new InvalidOperationException("Duplicate IDs accepted.");
    }

    private static void Decoder()
    {
        HidField F(ushort index, ushort page, ushort usage, ushort link = 0,
            bool button = false, int min = 0, int max = 1000, ushort bits = 16) =>
            new(1, index, page, usage, link, button, bits, min, max);
        var decoder = new ReportDecoder([
            F(0, 0x0D, 0x54, max: 5), F(1, 0x0D, 0x56, max: 65535),
            F(2, 0x0D, 0x51, 1), F(3, 1, 0x30, 1, min: -100),
            F(4, 1, 0x31, 1), F(5, 0x0D, 0x42, 1, button: true),
            F(6, 0x0D, 0x47, 1, button: true), F(7, 9, 1, button: true)
        ]);
        Native.HidData D(ushort index, uint raw) => new() { DataIndex = index, RawValue = raw };
        var fields = new[] { D(0, 1), D(1, 100), D(2, 0), D(3, 65526), D(4, 250), D(5, 1) };
        var packet = decoder.Decode(fields);
        var c = packet.Contacts.Single();
        Check(c.Id == 0 && c.X == -10 && c.Y == 250 && c.Tip && c.Confidence == false,
            "Decode zero ID, signed value and absent confidence.");
        Check(packet.Buttons == 0 && c.Pressure is null, "Missing fields must not invent data.");
        Check(Math.Abs(c.NormalizedX - 90.0 / 1100) < 1e-9, "Normalize with nonzero logical minimum.");
        packet = decoder.Decode(fields.Where(d => d.DataIndex != 5).Append(D(7, 1)));
        Check(!packet.Contacts[0].Tip && packet.Buttons == 1, "OFF tip and ON mechanical button.");
    }
}
