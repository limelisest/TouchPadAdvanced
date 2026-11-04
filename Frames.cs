namespace TouchPadAdvanced;

public sealed record Axis(int Min, int Max)
{
    internal double Normalize(int value) => Max > Min ? (value - (double)Min) / (Max - (double)Min) : 0;
}

public sealed record Contact(int Id, int X, int Y, bool Tip, bool? Confidence,
    int? Pressure, int? Width, int? Height, Axis XRange, Axis YRange)
{
    public double NormalizedX => XRange.Normalize(X);
    public double NormalizedY => YRange.Normalize(Y);
}

public sealed record ContactPacket(int Count, int ScanTime, uint Buttons, Contact[] Contacts);
public sealed record TouchFrame(DateTimeOffset ReceivedAt, int ScanTime, uint Buttons, Contact[] Contacts)
{
    public int ActiveCount => Contacts.Count(c => c.Tip);
}

// A frame can span several HID reports. Only its first report carries the total count;
// zero-count reports at the same scan time continue an unfinished frame.
public sealed class FrameAssembler
{
    private readonly List<Contact> pending = [];
    private int expected, scanTime;
    private uint buttons;
    internal long IncompleteFrames { get; private set; }

    internal TouchFrame? Push(ContactPacket packet, DateTimeOffset now)
    {
        if (packet.Count > 0)
        {
            DiscardPending();
            expected = packet.Count;
            scanTime = packet.ScanTime;
            buttons = packet.Buttons;
        }
        else if (expected == 0 || packet.ScanTime != scanTime)
        {
            DiscardPending();
            return new TouchFrame(now, packet.ScanTime, packet.Buttons, []);
        }

        int needed = expected - pending.Count;
        pending.AddRange(packet.Contacts.Take(needed));
        if (pending.Count < expected) return null;

        if (pending.Select(c => c.Id).Distinct().Count() != expected)
        {
            DiscardPending();
            throw new InvalidDataException("同一帧出现重复 Contact ID，已丢弃该帧。");
        }

        var frame = new TouchFrame(now, scanTime, buttons, pending.ToArray());
        pending.Clear();
        expected = 0;
        return frame;
    }

    private void DiscardPending()
    {
        if (expected != 0) IncompleteFrames++;
        pending.Clear();
        expected = 0;
    }
}
