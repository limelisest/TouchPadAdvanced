using System.Runtime.InteropServices;

namespace TouchPadAdvanced;

public sealed record HidField(byte ReportId, ushort DataIndex, ushort Page, ushort Usage,
    ushort Collection, bool Button, ushort Bits, int Min, int Max);

internal sealed class ReportDecoder
{
    private readonly HidField[] fields;
    private readonly HidField[][] fingers;
    private readonly HidField count, scan;

    internal ReportDecoder(HidField[] fields)
    {
        this.fields = fields;
        count = Required(fields, 0x0D, 0x54);
        scan = Required(fields, 0x0D, 0x56);
        fingers = fields.GroupBy(f => f.Collection)
            .Where(g => g.Any(f => f.Page == 0x0D && f.Usage == 0x51))
            .Select(g => g.ToArray()).OrderBy(g => Required(g, 0x0D, 0x51).DataIndex).ToArray();
        if (fingers.Length == 0) throw new InvalidDataException("报告没有 Finger / Contact ID。");
        foreach (var finger in fingers)
        {
            Required(finger, 1, 0x30);
            Required(finger, 1, 0x31);
            Required(finger, 0x0D, 0x42);
        }
    }

    internal ContactPacket Decode(IEnumerable<Native.HidData> data)
    {
        var values = data.ToDictionary(d => d.DataIndex, d => d.RawValue);
        int Read(HidField field)
        {
            if (!values.TryGetValue(field.DataIndex, out uint raw))
            {
                if (field.Button) return 0; // HidP_GetData omits OFF buttons.
                throw new InvalidDataException($"HID 数据缺少索引 {field.DataIndex}。");
            }
            if (field.Button) return (raw & 0xFF) != 0 ? 1 : 0;
            if (field.Min < 0 && field.Bits is > 0 and < 32)
            {
                int shift = 32 - field.Bits;
                return (int)(raw << shift) >> shift;
            }
            return unchecked((int)raw);
        }

        int? Optional(HidField[] group, ushort page, ushort usage)
        {
            var field = group.FirstOrDefault(f => f.Page == page && f.Usage == usage);
            return field is null ? null : Read(field);
        }

        int total = Read(count);
        if (total < 0 || total > count.Max)
            throw new InvalidDataException($"Contact Count 超出描述范围：{total}。");
        // A continuation has Count=0, so retain its contact slots for the assembler.
        int slots = total == 0 ? fingers.Length : Math.Min(total, fingers.Length);
        var contacts = fingers.Take(slots).Select(group =>
        {
            var x = Required(group, 1, 0x30);
            var y = Required(group, 1, 0x31);
            int? confidence = Optional(group, 0x0D, 0x47);
            return new Contact(Read(Required(group, 0x0D, 0x51)), Read(x), Read(y),
                Read(Required(group, 0x0D, 0x42)) != 0,
                confidence.HasValue ? confidence.Value != 0 : null,
                Optional(group, 0x0D, 0x30), Optional(group, 0x0D, 0x48),
                Optional(group, 0x0D, 0x49), new Axis(x.Min, x.Max), new Axis(y.Min, y.Max));
        }).ToArray();

        uint buttons = 0;
        foreach (var field in fields.Where(f => f.Page == 9 && f.Usage is >= 1 and <= 32))
            if (Read(field) != 0) buttons |= 1u << (field.Usage - 1);
        return new ContactPacket(total, Read(scan), buttons, contacts);
    }

    private static HidField Required(IEnumerable<HidField> source, ushort page, ushort usage) =>
        source.SingleOrDefault(f => f.Page == page && f.Usage == usage)
        ?? throw new InvalidDataException($"报告缺少 Usage {page:X2}:{usage:X2}。");
}

internal sealed class HidDescriptor : IDisposable
{
    private nint preparsed;
    private readonly Native.HidData[] data;
    private readonly Dictionary<byte, ReportDecoder> decoders = [];
    internal Native.Caps Caps { get; }
    internal HidField[] Fields { get; }
    internal bool CanDecodeContacts => decoders.Count > 0;

    internal HidDescriptor(nint device)
    {
        try
        {
            uint size = 0;
            Native.CheckWin32(Native.GetDeviceInfo(device, Native.PreparsedData, 0, ref size),
                "读取 HID 描述长度");
            preparsed = Marshal.AllocHGlobal(checked((int)size));
            Native.CheckWin32(Native.GetDeviceInfo(device, Native.PreparsedData, preparsed, ref size),
                "读取 HID 描述");
            Native.CheckHid(Native.HidP_GetCaps(preparsed, out var caps), "HidP_GetCaps");
            Caps = caps;
            var nodes = new Native.LinkNode[caps.LinkNodes];
            uint nodeCount = (uint)nodes.Length;
            Native.CheckHid(Native.HidP_GetLinkCollectionNodes(nodes, ref nodeCount, preparsed),
                "HidP_GetLinkCollectionNodes");

            ushort FingerCollection(ushort collection)
            {
                ushort current = collection;
                for (int hops = 0; current != 0 && hops < nodes.Length; hops++)
                {
                    var node = nodes[current];
                    if (node.UsagePage == 0x0D && node.Usage == 0x22) return current;
                    current = node.Parent;
                }
                return collection;
            }

            var fields = new List<HidField>();
            void AddCaps(Native.Capability[] capabilities, bool button)
            {
                foreach (var cap in capabilities)
                {
                    if (cap.IsAlias != 0) continue;
                    // Vendor value arrays are not contact fields and are not returned by HidP_GetData.
                    if (!button && cap.IsRange == 0 && cap.ReportCount > 1) continue;
                    int length = cap.IsRange != 0 ? cap.UsageMax - cap.UsageMin + 1 : 1;
                    for (int offset = 0; offset < length; offset++)
                        fields.Add(new HidField(cap.ReportId, (ushort)(cap.DataIndexMin + offset),
                            cap.UsagePage, (ushort)(cap.UsageMin + offset), FingerCollection(cap.LinkCollection),
                            button, button ? (ushort)1 : cap.BitSize,
                            button ? 0 : cap.LogicalMin, button ? 1 : cap.LogicalMax));
                }
            }

            ushort valueCount = caps.InputValues;
            if (valueCount != 0)
            {
                var values = new Native.Capability[valueCount];
                Native.CheckHid(Native.HidP_GetValueCaps(0, values, ref valueCount, preparsed),
                    "HidP_GetValueCaps");
                AddCaps(values.Take(valueCount).ToArray(), false);
            }
            ushort buttonCount = caps.InputButtons;
            if (buttonCount != 0)
            {
                var buttons = new Native.Capability[buttonCount];
                Native.CheckHid(Native.HidP_GetButtonCaps(0, buttons, ref buttonCount, preparsed),
                    "HidP_GetButtonCaps");
                AddCaps(buttons.Take(buttonCount).ToArray(), true);
            }

            Fields = fields.ToArray();
            foreach (var group in Fields.GroupBy(f => f.ReportId))
                if (group.Any(f => f.Page == 0x0D && f.Usage == 0x54))
                    decoders.Add(group.Key, new ReportDecoder(group.ToArray()));
            data = new Native.HidData[Native.HidP_MaxDataListLength(0, preparsed)];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal ContactPacket? Parse(byte[] report)
    {
        if (report.Length == 0 || !decoders.TryGetValue(report[0], out var decoder)) return null;
        if (report.Length != Caps.InputLength)
            throw new InvalidDataException($"报告长度 {report.Length}，描述长度 {Caps.InputLength}。");
        uint length = (uint)data.Length;
        Native.CheckHid(Native.HidP_GetData(0, data, ref length, preparsed, report, (uint)report.Length),
            "HidP_GetData");
        return decoder.Decode(data.Take((int)length));
    }

    public void Dispose()
    {
        if (preparsed == 0) return;
        Marshal.FreeHGlobal(preparsed);
        preparsed = 0;
    }
}
