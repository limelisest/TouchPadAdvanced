using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace TouchPadAdvanced;

internal static class MouseSourceProbe
{
    internal static int Run(int seconds, string output)
    {
        using var source = new RawInputSource();
        var observations = new Queue<object>();
        long hookCount=0;
        using var hook = new MouseInputHook(mouse =>
        {
            hookCount++;
            var device=source.Devices.FirstOrDefault(d=>d.CanDecodeContacts);
            var frame=device?.Frame;
            observations.Enqueue(new
            {
                mouse.Message, mouse.X, mouse.Y, mouse.Flags, mouse.ExtraInfo, mouse.Time,
                mouse.DeviceType, mouse.Origin, mouse.SourceAvailable, mouse.Timestamp,
                deviceReports=device?.ReportCount,
                activeContacts=frame?.ActiveCount,
                contacts=frame?.Contacts.Select(c=>new{c.Id,c.Tip,c.NormalizedX,c.NormalizedY}).ToArray()
            });
            while(observations.Count>2048)observations.Dequeue();
            return false;
        });
        nuint timer=Native.SetTimer(source.Handle,3,(uint)seconds*1000,0);
        if(timer==0)throw new Win32Exception();
        try
        {
            while(true)
            {
                int result=Native.GetMessage(out var message,0,0,0);
                if(result<0)throw new Win32Exception();
                if(result==0 || (message.Window==source.Handle && message.Message==0x113 && message.WParam==timer))break;
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        finally{Native.KillTimer(source.Handle,timer);}
        File.WriteAllText(output,JsonSerializer.Serialize(new
        {
            hookCount, observations=observations.ToArray(), devices=source.Devices.Select(d=>new{d.Label,d.ReportCount,d.FrameCount,d.ErrorCount})
        },Diagnostics.JsonOptions));
        Console.WriteLine($"hooks={hookCount}, frames={source.Devices.Sum(d=>d.FrameCount)}");
        return 0;
    }
}

