using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Plugin;
using TvAIr.Schedule;
using TvAIr.Tuner;
using TvAIrPlugin;
using TvAIrPlugin.Runtime;

internal static class Program
{
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
    static readonly BindingFlags Static=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static;
    static readonly List<string> Trace=new();
    static void Check(bool ok,string text){Trace.Add((ok?"PASS ":"FAIL ")+text);Console.WriteLine(Trace.Last());if(!ok)throw new Exception(text);}
    [STAThread] static int Main(string[] args)
    {
        if(args.Length>0 && args[0].StartsWith('/')){try{Receiver.Run(args);return 0;}catch(Exception ex){File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"receiver-boot-"+Environment.ProcessId+".txt"),ex.ToString());return 1;}}
        if(args.Length==0)
        {
            var desktop=Native.CreateDesktop("TvAIrFocusTest_Simulation_"+Guid.NewGuid().ToString("N"),null,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
            if(desktop==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            try{var exit=0;foreach(var group in new[]{"HYBRID","CATV"}){using var child=Native.Start(Environment.ProcessPath!,new[]{"run",group},Native.DesktopName(desktop));if(!child.WaitForExit(55000)){child.Kill();throw new TimeoutException();}if(Native.ExitCode(child)!=0)exit=1;}return exit;}finally{Native.CloseDesktop(desktop);}
        }
        try{Run(args[1]).GetAwaiter().GetResult();return 0;}catch(Exception ex){Trace.Add(ex.ToString());Console.WriteLine(ex);return 1;}
        finally{File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"simulation-results-"+args[1]+".json"),JsonSerializer.Serialize(Trace,new JsonSerializerOptions{WriteIndented=true}));}
    }
    static object Create(Type t,params object?[] args)=>Activator.CreateInstance(t,Private,null,args,null)!;
    static async Task<string> ReadReceiver(string file,string expected){for(int i=0;i<100;i++){try{var text=File.ReadAllText(file);if(text.Contains(expected))return text;}catch(IOException){}await Task.Delay(10);}return "";}
    static async Task Run(string physicalGroup)
    {
        Check(Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())).StartsWith("TvAIrFocusTest_Simulation_"),"isolated desktop; production TVTest is never launched");
        var root=Path.Combine(AppContext.BaseDirectory,"fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TVAIR_SIM_RECEIVER_LOG_DIR",root);
        var ch2=Path.Combine(root,"BonDriver_Sim.ch2");
        File.WriteAllText(ch2,"GR,0,10,0,1,101,30848,100,1\nBS,1,20,0,1,201,4,200,1\nCable1,2,90,0,1,501,65534,500,1\nCable2,2,90,0,1,502,65534,500,1\nDisabled,3,91,0,1,601,65533,600,0\n",Encoding.UTF8);
        var ini=new IniSettingsService(root,Path.Combine(root,"data"),18787);
        foreach(var name in new[]{"TvTestExecutablePath","ViewingTvTestExecutablePath"})typeof(IniSettingsService).GetProperty(name)!.SetValue(ini,Environment.ProcessPath!);
        ini.Tuners.Clear();ini.Tuners.Add(new TunerProfileDto{Name="View",BonDriverFileName="BonDriver_Sim.dll",ChannelCh2Path=ch2,Group=physicalGroup,Role="Viewing",Did="B",DeviceNumber=1,LogicalViewerSlotId="sim"});
        var tuner=new TunerProfile{Name="View",BonDriverFileName="BonDriver_Sim.dll",ChannelCh2Path=ch2,Group=physicalGroup,Role="Viewing",Did="B",DeviceNumber=1,LogicalViewerSlotId="sim"};
        var profiles=new[]{tuner};var settings=new TvTestSettings{ExecutablePath=Environment.ProcessPath!};
        var loader=new ChannelFileLoader(Options.Create(new ChannelMapSettings()),ini);var policy=new ChannelServiceAccessPolicy(loader);
        var log=new LogRepository();var appGate=new ApplicationOperationGate(log);var pool=new TunerPool(profiles,ini,log);
        var leases=new ExternalTunerLeaseService(pool,log,appGate,policy);
        using var events=new PluginTypedEventHub(log,new ReservationMutationJournal(),loader);
        var sessions=new ViewerSessionRegistry(log,events);var launcher=new TvTestLauncher(ini,Options.Create(settings),log);
        var ownership=new ViewerOwnershipService(leases,launcher,log);
        var operations=new ViewerOperationService(policy,leases,pool,sessions,ownership,Options.Create(settings),ini,profiles,log,appGate);
        var host=typeof(ChannelFileLoader).Assembly;var capabilities=host.GetType("TvAIr.Plugin.PluginCapabilityContext")!;
        var gate=Create(capabilities.GetNestedType("CapabilityPermissionGate",BindingFlags.NonPublic)!,"aircon.basic","Simulation",log,Enum.GetValues<PluginPermission>());
        var readModels=Create(host.GetType("TvAIr.Plugin.PluginReadModelSource")!,null,loader,null,pool);
        var channels=(ITvAirChannelsApi)Create(capabilities.GetNestedType("ChannelsApi",BindingFlags.NonPublic)!,policy,gate);
        var hostViewers=(TvAIrPlugin.ITvAirViewersApi)Create(capabilities.GetNestedType("ViewersApi",BindingFlags.NonPublic)!,"aircon.basic","Simulation",leases,sessions,operations,new ViewerOperationPreemptionHub(log),readModels,policy,settings,ini,profiles,events,gate);
        var adapter=host.GetType("TvAIr.Plugin.PluginRuntimeContext")!.GetNestedType("RuntimeViewersApi",BindingFlags.NonPublic)!;
        var runtimeViewers=(TvAIrPlugin.Viewers.ITvAirViewersApi)Create(adapter,hostViewers);
        var context=DispatchProxy.Create<ITvAirPluginRuntimeContext,Proxy>();
        var logs=DispatchProxy.Create<ITvAirLogsApi,Proxy>();
        ((Proxy)(object)logs).Call=(m,a)=>{if(m.Name=="Write"&&a?[0] is TvAirLogWriteDto item)log.Add("SIM_PLUGIN_LOG",item.Category,item.Message);return null;};
        ((Proxy)(object)context).Call=(m,a)=>m.Name switch{"get_Channels"=>channels,"get_Viewers"=>runtimeViewers,"get_Logs"=>logs,_=>null};
        var plugin=typeof(AIrCon.BasicPlugin.AIrConRuntimePlugin).Assembly;
        var bridge=plugin.GetType("AIrCon.BasicPlugin.AIrConNewApiBridge")!;
        bridge.GetField("_runtimeContext",Static)!.SetValue(null,context);
        var rendererType=plugin.GetType("AIrCon.BasicPlugin.AIrConRenderer")!;var renderer=Create(rendererType);
        var profile=runtimeViewers.ListProfiles().Single();var service=channels.ListServices(new(){BroadcastType="CATV"}).First();
        Check(profile.BroadcastGroups.Contains("CATV")&&service.ServiceId==501,"real Channels API -> real Host profile -> real runtime adapter exposes CATV");
        var renderContext=new RuntimeUiRenderContext{PluginId="aircon.basic",UiDefinitionId="simulation",Route="aircon",ActionToken="simulation-token",ActionEndpoint="/api/plugins/action"};
        var captured=rendererType.GetMethod("CaptureViewerProfiles",Private)!.Invoke(renderer,new[]{renderContext})!;
        var selected=rendererType.GetMethod("ResolveSelectedViewerProfile",Static)!.Invoke(null,new[]{(object)profile.ViewerProfileId,captured,"CATV"})!;
        var row=rendererType.GetMethod("FromRuntimeService",Static)!.Invoke(null,new object[]{service,0})!;
        object Nested(string name,params object?[] values)=>Create(rendererType.GetNestedType(name,BindingFlags.NonPublic)!,values);
        var action=Nested("ViewerOperation",true,"/api/plugins/action","aircon","POST","simulation-token","aircon.basic","aircon");
        var window=Nested("WindowOperation",true,true,"/api/plugins/window","aircon","POST","simulation-token","aircon.basic","aircon","simulation-window","",true);
        var auto=rendererType.GetNestedType("TunerChoice",BindingFlags.NonPublic)!.GetProperty("Auto")!.GetValue(null);
        var html=(string)rendererType.GetMethod("BuildServiceRow",Static)!.Invoke(null,new[]{renderContext,row,action,window,auto,selected,null,(object)0,false})!;
        File.WriteAllText(Path.Combine(root,"catv-row.html"),html);
        Check(html.Contains("data-aircon-viewer-tune")&&!html.Contains("対応する視聴用チューナーが設定されていません"),"real profile capture/selection/row renderer enables the CATV double-click action");
        async Task<RuntimeUiActionResult> Click(int sid,string wave="CATV")
        {
            var payload=new Dictionary<string,string>{{"operation","viewerTune"},{"viewerProfile",profile.ViewerProfileId},{"wave",wave},{"nid",sid==101?"30848":sid==201?"4":sid==601?"65533":"65534"},{"tsid",sid==101?"100":sid==201?"200":sid==601?"600":"500"},{"sid",sid.ToString()}};
            var task=(Task<RuntimeUiActionResult>)rendererType.GetMethod("HandleActionAsync")!.Invoke(renderer,new object[]{new RuntimeUiActionContext{PluginId="aircon.basic",UiDefinitionId="simulation",Route="aircon",ActionName="pluginOwnedAction",Payload=payload},CancellationToken.None})!;
            var result=await task;Trace.Add("ACTION "+result.Succeeded+" "+result.Diagnostics+" "+result.Message);return result;
        }
        try
        {
            Check((await Click(501)).Succeeded,"AIrCon double-click -> adapter -> Host -> lease -> launch -> session -> UI success");
            var first=runtimeViewers.ListSessions().Single();
            Check(first.ProcessId>0&&first.CurrentService?.ServiceId==501,"real process ownership and runtime session carry requested identity");
            Trace.Add("WINDOWS "+string.Join(";",Native.Windows(first.ProcessId!.Value).Select(Native.Caption))); var file=Path.Combine(root,"receiver-"+first.ProcessId+".jsonl");
            for(int i=0;i<100&&!File.Exists(file);i++)await Task.Delay(10);
            var initial=await ReadReceiver(file,"/sid 501");
            Check(initial.Contains("/chspace 2")&&initial.Contains("/chi 90")&&initial.Contains("/sid 501")&&initial.Contains("/DID B")&&initial.Contains("BonDriver_Sim.dll"),"receiver observes exact driver/DID/chspace/chi/SID from real launcher");
            Check((await Click(502)).Succeeded,"second CATV click uses real PID-targeted retune and returns success");
            var second=runtimeViewers.ListSessions().Single();
            Check(second.ProcessId==first.ProcessId&&second.CurrentService?.ServiceId==502&&second.Generation>first.Generation,"retune preserves one process and advances session identity/generation");
            Check((await ReadReceiver(file,"/sid 502")).Contains("/sid 502"),"mock receiver actually received real WM_COPYDATA retune");
            Check(!(await Click(999)).Succeeded,"missing service rejected through same UI pipeline");
            Check(!(await Click(601)).Succeeded,"disabled CATV service rejected through same UI pipeline");
            Check(runtimeViewers.ListSessions().Single().CurrentService?.ServiceId==502,"failed request does not corrupt existing session");
            Check((await Click(101,"GR")).Succeeded,"GR retune remains functional on same physical viewer");
            Check((await Click(201,"BS")).Succeeded,"BS retune remains functional on same physical viewer");
            var stopRequest=new RuntimeUiActionContext{PluginId="aircon.basic",UiDefinitionId="simulation",Route="aircon",ActionName="pluginOwnedAction",Payload=new Dictionary<string,string>{{"operation","viewerPowerOff"},{"viewerProfile",profile.ViewerProfileId},{"wave","BS"}}};
            var stopped=await (Task<RuntimeUiActionResult>)rendererType.GetMethod("HandleActionAsync")!.Invoke(renderer,new object[]{stopRequest,CancellationToken.None})!;
            Console.WriteLine("STOP "+stopped.Succeeded+" "+stopped.Diagnostics+" "+stopped.Message);
            Check(stopped.Succeeded&&runtimeViewers.ListSessions().Count==0&&leases.GetActiveLeases().Count==0,"AIrCon stop -> real Host process close -> session removal -> lease release");
        }
        finally
        {
            foreach(var lease in leases.GetActiveLeases())if(lease.ProcessId>0)ownership.RollbackNewOwnership(lease.LeaseId,lease.ProcessId.Value,"simulation_cleanup"); File.WriteAllText(Path.Combine(root,"host-log.json"),JsonSerializer.Serialize(log.GetAll()));
        }
        Check(leases.GetActiveLeases().Count==0,"stop/cleanup releases physical tuner lease");
        ini.Tuners[0].Role="Recording";
        bridge.GetMethod("InvalidateViewerProjection",Static)!.Invoke(null,null);
        Check(runtimeViewers.ListProfiles().Count==0&&!(await Click(501)).Succeeded,"recording-only configuration does not start a viewer or borrow viewing capability");
        Check(leases.GetActiveLeases().Count==0,"rejected stale UI request creates no lease");
    }
}
public class Proxy:DispatchProxy{public Func<MethodInfo,object?[]?,object?> Call{get;set;}=(_,_)=>null;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Call(m!,a);}
internal sealed class Receiver:Form
{
    delegate IntPtr WindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
    static readonly WindowProc NativeProc=Dispatch;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct Class{public uint style;public IntPtr proc;public int clsExtra,winExtra;public IntPtr instance,icon,cursor,background;public string? menu;public string name;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern ushort RegisterClass(ref Class c);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr CreateWindowEx(uint ex,string name,string text,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr args);
    [DllImport("user32.dll")]static extern void PostQuitMessage(int code);
    static void NativeRecord(string text)=>File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("TVAIR_SIM_RECEIVER_LOG_DIR")!,"receiver-"+Environment.ProcessId+".jsonl"),JsonSerializer.Serialize(text)+Environment.NewLine);
    static IntPtr Dispatch(IntPtr h,uint m,IntPtr w,IntPtr l){if(m==0x004A){var data=Marshal.PtrToStructure<Data>(l);NativeRecord(Marshal.PtrToStringUni(data.text)??"");return new IntPtr(1);}if(m==0x0002)PostQuitMessage(0);return DefWindowProc(h,m,w,l);}
    public static void Run(string[] args){Register();var h=CreateWindowEx(0,"TVTest","Simulated TVTest receiver",0x10CF0000,100,100,300,160,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);if(h==IntPtr.Zero)throw new Exception("mock native receiver creation failed");NativeRecord("OWN_WINDOW="+h+" "+string.Join(" ",args));Application.Run(new ApplicationContext());}
    public static void Register(){var c=new Class{style=0x4000,proc=Marshal.GetFunctionPointerForDelegate(NativeProc),instance=GetModuleHandle(null),name="TVTest"};if(RegisterClass(ref c)==0)throw new Exception("mock window class registration failed");}
    readonly string path=Path.Combine(Environment.GetEnvironmentVariable("TVAIR_SIM_RECEIVER_LOG_DIR")!,"receiver-"+Environment.ProcessId+".jsonl");
    readonly string[] args;
    protected override CreateParams CreateParams{get{var cp=base.CreateParams;cp.ClassName="TVTest";return cp;}}
    public Receiver(string[] args){this.args=args;Text="Simulated TVTest receiver";Shown+=(_,_)=>{Record("OWN_WINDOW="+Handle+" "+Text+" "+string.Join(" ",args));};}
    void Record(string command)=>File.AppendAllText(path,JsonSerializer.Serialize(command)+Environment.NewLine);
    [StructLayout(LayoutKind.Sequential)]struct Data{public UIntPtr kind;public int bytes;public IntPtr text;}
    protected override void WndProc(ref Message m){if(m.Msg==0x004A){var data=Marshal.PtrToStructure<Data>(m.LParam);Record(Marshal.PtrToStringUni(data.text)??"");m.Result=new IntPtr(1);return;}base.WndProc(ref m);}
}
