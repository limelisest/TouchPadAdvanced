using System.ComponentModel;
using System.Text.Json;
using TouchPadAdvanced;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--generate-icon")
            {
                IconGenerator.SaveIcon(args[1]);
                Console.WriteLine($"Icon generated: {args[1]}");
                return 0;
            }
            if (args.Length >= 1 && args[0] == "--self-test")
            {
                var result = SelfTests.Run();
                string json = JsonSerializer.Serialize(result, Diagnostics.JsonOptions);
                if (args.Length==3 && args[1]=="--output") File.WriteAllText(args[2],json);
                Console.WriteLine(json);
                return result.Passed?0:1;
            }
            if(args.Length==4 && args[0]=="--mouse-probe" && int.TryParse(args[1],out int probeSeconds) && probeSeconds is >=1 and <=300 && args[2]=="--output")
                return MouseSourceProbe.Run(probeSeconds,args[3]);
            if(args.Length==4 && args[0]=="--probe" && int.TryParse(args[1],out int seconds) &&
                seconds is >=1 and <=300 && args[2]=="--output")
            {
                using var source=new RawInputSource();
                using var runtime=new GestureRuntime(source,new GestureSettings { ObserveOnly=true });
                nuint timer=Native.SetTimer(source.Handle,1,(uint)(seconds*1000),0);
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
                finally { Native.KillTimer(source.Handle,timer); }
                Diagnostics.Save(source,args[3],runtime);
                Console.WriteLine($"devices={source.Devices.Count}, frames={source.Devices.Sum(d=>d.FrameCount)}, gestures={runtime.ActivityVersion}");
                return source.Devices.Any(d=>d.FrameCount>0)?0:2;
            }
            Console.Error.WriteLine("Use --self-test [--output file] or --probe seconds --output file.");
            return 1;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
