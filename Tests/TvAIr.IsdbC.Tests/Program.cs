using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using TvAIr.Channel;
using TvAIr.Core;
using TvAIr.Epg;
using TvAIr.Plugin;
using TvAIr.Schedule;
using TvAIr.Tuner;

// No BonDriver, TVTest or tuner is opened. All cases use synthetic .ch2 and SQLite stores.
var root = Path.Combine(Path.GetFullPath(args.FirstOrDefault() ?? Path.GetTempPath()), "TvAIr-IsdbC-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var results = new List<object>();
var failed = 0;
void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
void Case(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); results.Add(new { name, passed = true }); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); results.Add(new { name, passed = false, error = ex.ToString() }); }
}
string Ch2(string name, string rows, Encoding? encoding = null)
{
    var path = Path.Combine(root, name + ".ch2");
    File.WriteAllText(path, "; synthetic channel fixture\n" + rows, encoding ?? Encoding.GetEncoding(932));
    return path;
}
string Row(string name, int space, int channel, ushort nid, ushort tsid, ushort sid, bool enabled = true)
    => $"{name},{space},{channel},0,1,{sid},{nid},{tsid},{(enabled ? 1 : 0)}\n";
TunerProfile Profile(string name, string driver, string path, string group = "HYBRID", string role = "Recording")
    => new() { Name = name, BonDriverFileName = driver, ChannelCh2Path = path, Group = group, Role = role, Did = "1", LogicalViewerSlotId = name.ToLowerInvariant() };
TunerProfileDto Dto(TunerProfile p) => new() { Name = p.Name, BonDriverFileName = p.BonDriverFileName, ChannelCh2Path = p.ChannelCh2Path,
    Group = p.Group, Role = p.Role, Did = p.Did, LogicalViewerSlotId = p.LogicalViewerSlotId };
(IniSettingsService Ini, ChannelFileLoader Loader, ChannelServiceAccessPolicy Policy, ReservationStore Store, TunerPool Pool, NormalEpgWaveOccupation Occupation, Database Db)
    Context(string name, params TunerProfile[] profiles)
{
    var dir = Path.Combine(root, name); Directory.CreateDirectory(dir);
    var ini = new IniSettingsService(dir, Path.Combine(dir, "data"), 18787);
    ini.Tuners.Clear(); ini.Tuners.AddRange(profiles.Select(Dto));
    var loader = new ChannelFileLoader(Options.Create(new ChannelMapSettings()), ini);
    var policy = new ChannelServiceAccessPolicy(loader);
    var db = new Database(Path.Combine(dir, "data"));
    var log = new LogRepository();
    var occupation = new NormalEpgWaveOccupation();
    var store = new ReservationStore(db, log, new ChainRuntimeSnapshotRegistry(), policy, new ReservationMutationJournal(), occupation);
    return (ini, loader, policy, store, new TunerPool(profiles, ini, log), occupation, db);
}
var at = DateTime.Now.AddDays(2).Date.AddHours(12);
Reservation Reserve(ReservationStore store, ushort nid, ushort tsid, ushort sid, ReservationSource source = ReservationSource.Manual, int start = 0, int duration = 30)
{
    var r = new Reservation { NetworkId = nid, TransportStreamId = tsid, ServiceId = sid, EventId = 100,
        Source = source, StartTime = at.AddMinutes(start), EndTime = at.AddMinutes(start + duration), Title = "Synthetic", ServiceName = "Fixture" };
    r.Id = store.Add(r); return r;
}
void Evaluate(ReservationStore store, TunerPool pool, TunerProfile[] profiles, bool later = false, bool chain = false, int margin = 0)
    => store.ReevaluateConflicts(profiles, later, chain, chain, margin, pool, margin);
object CaptureForPlan(TunerProfile[] profiles)
{
    var capture = RuntimeHelpers.GetUninitializedObject(typeof(EpgCapture));
    typeof(EpgCapture).GetField("tunerProfiles", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(capture, profiles);
    typeof(EpgCapture).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(capture, new LogRepository());
    return capture;
}
object Invoke(object target, string method, params object?[] values)
    => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, values)!;
object[] Items(object value) => ((IEnumerable)value).Cast<object>().ToArray();
T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;

var rows = Row("地上波", 3, 19, 0x7880, 100, 101) + Row("衛星", 7, 41, 4, 200, 201)
    + Row("CATV", 9, 80, 0xfffe, 300, 301);
var a = Ch2("driver-a", rows);
var b = Ch2("driver-b", Row("別経路", 12, 90, 4, 200, 201));
var service = new BroadcastServiceKey(4, 200, 201);
Case("driver coordinates are independent", () => {
    var cat = BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", a), new BonDriverChannelSource("BonDriver_B.dll", b) });
    Check(cat.TryResolve("BonDriver_A.dll", service, out var ar) && ar.TuningSpace == 7 && ar.ChannelIndex == 41, "A coordinate");
    Check(cat.TryResolve("BonDriver_B.dll", service, out var br) && br.TuningSpace == 12 && br.ChannelIndex == 90, "B coordinate");
    Check(!cat.TryResolve("BonDriver_Unknown.dll", service, out _), "foreign driver fallback");
});
Case("CSV names and enabled rows CP932", () => {
    var path = Ch2("quotes", Row("\"局,\"\"名称\"\"\"", 2, 6, 4, 200, 201) + Row("OFF", 2, 6, 4, 200, 202, false) + "bad,-1,0,0,1,203,4,200,1\n");
    var cat = BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", path) });
    Check(cat.SourceRoutes.Count == 1 && cat.SourceRoutes[0].ServiceName == "局,\"名称\"", "CSV quote/state parsing");
});
foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode })
    Case("BOM " + encoding.WebName, () => {
        var path = Ch2(encoding.WebName, Row("日本語", 1, 8, 4, 200, 201), encoding);
        var cat = BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", path) });
        Check(cat.SourceRoutes.Single().ServiceName == "日本語", "BOM decoding");
    });
Case("ambiguous transport fails closed", () => {
    var path = Ch2("collision", Row("one", 1, 8, 4, 200, 201) + Row("two", 1, 9, 4, 200, 201) + Row("sibling", 1, 8, 4, 200, 202));
    var cat = BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", path) });
    Check(!cat.TryResolve("BonDriver_A.dll", service, out _) && !cat.TryResolve("BonDriver_A.dll", new(4,200,202), out _), "ambiguous TS admitted");
    var siblings = Ch2("siblings", Row("one", 1, 8, 4, 200, 201) + Row("two", 1, 9, 4, 200, 202));
    Check(BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", siblings) }).SourceRoutes.Count == 0, "inconsistent sibling coordinates");
});
Case("identical duplicate is harmless", () => {
    var row = Row("one", 1, 8, 4, 200, 201);
    var path = Ch2("duplicate", row + row);
    Check(BonDriverChannelRouteCatalog.FromCh2Sources(new[] { new BonDriverChannelSource("BonDriver_A.dll", path) }).SourceRoutes.Count == 1, "identical duplicate rejected");
});
Case("invalid source does not disable other drivers", () => {
    var cat = BonDriverChannelRouteCatalog.FromConfiguredTuners(new[] {
        Dto(Profile("A", "BonDriver_A.dll", a)), Dto(Profile("bad", "BonDriver_Missing.dll", Path.Combine(root,"missing.ch2"))) });
    Check(cat.TryResolve("BonDriver_A.dll", service, out _) && cat.SourceErrors.Count == 1, "missing source handling");
    cat = BonDriverChannelRouteCatalog.FromConfiguredTuners(new[] { Dto(Profile("A1","BonDriver_A.dll",a)), Dto(Profile("A2","BonDriver_A.dll",b)), Dto(Profile("B","BonDriver_B.dll",b)) });
    Check(!cat.TryResolve("BonDriver_A.dll", service, out _) && cat.TryResolve("BonDriver_B.dll", service, out _), "conflicting source handling");
});
Case("display classifies unknown cable identity separately", () => {
    var p = Profile("H1", "BonDriver_A.dll", a); var ctx = Context("display", p);
    var display = ctx.Policy.Capture().GetProgramGuideDisplayTargets(ctx.Ini.Tuners);
    Check(display.Single(x=>x.OriginalNetworkId==0xfffe).Group == "HYBRID", "unknown cable classified as GR");
    Check(display.Single(x=>x.OriginalNetworkId==0x7880).Group == "GR" && display.Single(x=>x.OriginalNetworkId==4).Group=="BSCS", "known wave classification");
    Check(display.All(x=>x.ChannelArgument.Length==0), "display exports globally usable driver coordinates");
});
Case("one hybrid physical lease spans waves and rejects stale ownership", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("leases",p);
    var lease=ctx.Pool.AcquireForRecording("GR", 1, at)!; Check(lease is not null,"GR acquire");
    Check(ctx.Pool.AcquireForRecording("BSCS",2,at) is null, "one tuner double leased");
    Check(ctx.Policy.Capture().TryResolveDirectTargetForLease(lease!,4,200,201,"BSCS",out var target) && target.ResolvedChannelIndex==41,"actual lease coordinates");
    lease!.Dispose(); Check(!ctx.Policy.Capture().TryResolveDirectRouteForLease(lease,4,200,201,out _),"stale lease route admitted");
    using var next=ctx.Pool.AcquireForRecording("BSCS",2,at); Check(next is not null,"release did not free tuner");
});
Case("recording capability cannot be supplied by viewing role", () => {
    var p=Profile("view","BonDriver_A.dll",a,role:"Viewing"); var ctx=Context("view-only",p);
    Check(!ctx.Policy.Capture().TryResolveRecordingAdmission(4,200,201,ctx.Ini.Tuners,out _,out _),"viewing admitted recording");
    var r=Reserve(ctx.Store,4,200,201); Evaluate(ctx.Store,ctx.Pool,new[]{p});
    Check(ctx.Store.GetById(r.Id)!.TunerName=="" && !ctx.Policy.Capture().IsOperationalForRecording(r,Array.Empty<TunerProfile>()),"viewing counted recording capacity");
});
foreach (var reverse in new[] {false,true})
    Case("global priority across GR BSCS CATV " + reverse, () => {
        var p=Profile("actual-cable-1","BonDriver_A.dll",a); var ctx=Context("global-"+reverse,p);
        Reservation manual, keyword, program;
        if(reverse) { manual=Reserve(ctx.Store,4,200,201); program=Reserve(ctx.Store,0xfffe,300,301,ReservationSource.Program); keyword=Reserve(ctx.Store,0x7880,100,101,ReservationSource.Keyword); }
        else { keyword=Reserve(ctx.Store,0x7880,100,101,ReservationSource.Keyword); program=Reserve(ctx.Store,0xfffe,300,301,ReservationSource.Program); manual=Reserve(ctx.Store,4,200,201); }
        Evaluate(ctx.Store,ctx.Pool,new[]{p});
        Check(!ctx.Store.GetById(manual.Id)!.IsConflicted && ctx.Store.GetById(manual.Id)!.TunerName==p.Name,"highest priority not actual physical name");
        Check(ctx.Store.GetById(keyword.Id)!.IsConflicted && ctx.Store.GetById(program.Id)!.IsConflicted,"hybrid triple counted");
    });
Case("two hybrid tuners permit two simultaneous reservations", () => {
    var profiles=new[]{Profile("H1","BonDriver_A.dll",a), Profile("H2","BonDriver_A.dll",a)}; profiles[1].Did="2";
    var ctx=Context("two-capacity",profiles); var r1=Reserve(ctx.Store,0x7880,100,101,ReservationSource.Program);
    var r2=Reserve(ctx.Store,4,200,201,ReservationSource.Keyword); var r3=Reserve(ctx.Store,0xfffe,300,301);
    Evaluate(ctx.Store,ctx.Pool,profiles); var all=new[]{r1,r2,r3}.Select(r=>ctx.Store.GetById(r.Id)!).ToArray();
    Check(all.Count(r=>!r.IsConflicted)==2 && all.Where(r=>!r.IsConflicted).Select(r=>r.TunerName).Distinct().Count()==2,"wrong physical capacity");
    Check(ctx.Store.GetById(r1.Id)!.IsConflicted,"lower priority did not lose");
});
Case("compatible repacking preserves higher priority", () => {
    var exclusive=Ch2("exclusive",Row("BS only",12,90,4,200,201));
    var profiles=new[]{Profile("flex","BonDriver_A.dll",a),Profile("bs-only","BonDriver_B.dll",exclusive,"BSCS")};
    var ctx=Context("repack",profiles); var gr=Reserve(ctx.Store,0x7880,100,101,ReservationSource.Keyword); var bs=Reserve(ctx.Store,4,200,201);
    Evaluate(ctx.Store,ctx.Pool,profiles);
    var g=ctx.Store.GetById(gr.Id)!; var s=ctx.Store.GetById(bs.Id)!;
    Check(!g.IsConflicted && !s.IsConflicted && g.TunerName=="flex" && s.TunerName=="bs-only","repack lost feasible assignment or used foreign source");
});
Case("missing service route remains unassigned", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("missing-service",p);
    var r=Reserve(ctx.Store,4,999,201); Evaluate(ctx.Store,ctx.Pool,new[]{p});
    Check(ctx.Store.GetById(r.Id)!.TunerName=="" && !ctx.Policy.Capture().CanTune(p,r),"foreign service fallback");
});
Case("known wave must fit configured physical capability", () => {
    var p=Profile("GR-only","BonDriver_A.dll",a,"GR"); var ctx=Context("incompatible",p);
    Check(!ctx.Policy.Capture().TryResolveRecordingAdmission(4,200,201,ctx.Ini.Tuners,out _,out _),"BS admitted to GR tuner");
    var r=Reserve(ctx.Store,4,200,201); Evaluate(ctx.Store,ctx.Pool,new[]{p}); Check(ctx.Store.GetById(r.Id)!.TunerName=="" && !ctx.Policy.Capture().CanTune(p,r),"incompatible physical assigned");
});
Case("normal EPG occupation preserves hybrid wave and generation", () => {
    var occupation=new NormalEpgWaveOccupation(); occupation.Set("GR",at,at.AddMinutes(10),"fixture",false,1);
    occupation.Set("HYBRID",at,at.AddMinutes(10),"fixture",false,2);
    Check(occupation.Snapshot().Count==2 && occupation.TryGet("HYBRID",out var h) && h.RunGeneration==2,"HYBRID overwrote GR");
    Check(!occupation.Clear("HYBRID",1) && occupation.Clear("HYBRID",2) && occupation.TryGet("GR",out _),"cross-generation clear");
});
Case("EPG occupying hybrid blocks all services of that physical tuner", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("epg-block",p);
    ctx.Occupation.Set("HYBRID",at,at.AddMinutes(40),"fixture",false,7);
    var r=Reserve(ctx.Store,4,200,201); Evaluate(ctx.Store,ctx.Pool,new[]{p});
    Check(ctx.Store.GetById(r.Id)!.IsConflicted,"hybrid EPG ignored by satellite reservation");
});
Case("direct normal EPG and PreRec use bound lease routes", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("epg-plan",p); var capture=CaptureForPlan(new[]{p});
    var groups=Items(Invoke(capture,"BuildNormalEpgGroups",ctx.Loader.Load(),false));
    Check(groups.Length==3 && groups.Select(g=>Get<string>(g,"Group")).Order().SequenceEqual(new[]{"BSCS","GR","HYBRID"}),"normal EPG scope");
    using var lease=ctx.Pool.AcquireForRecording("BSCS",1,at)!;
    var pre=Items(Invoke(capture,"BuildPreRecordGroups",ctx.Loader.Load(),lease,service));
    var target=Get<IReadOnlyList<ChannelTarget>>(pre.Single(),"Targets").Single();
    Check(target.ResolvedSpace==7 && target.ResolvedChannelIndex==41 && target.ServiceId==201,"PreRec coordinates");
    Check(Items(Invoke(capture,"BuildPreRecordGroups",ctx.Loader.Load(),lease,new BroadcastServiceKey(4,999,201))).Length==0,"PreRec foreign fallback");
});
Case("legacy plus direct EPG retains driver alternatives", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("epg-alternative",p); var load=ctx.Loader.Load();
    load.Targets.Add(new ChannelTarget { Group="BSCS",OriginalNetworkId=4,TransportStreamId=200,ServiceId=201,Name="legacy",ResolvedSpace=0,ResolvedChannelIndex=1,ChannelArgument="/chspace 0 /chi 1" });
    var groups=Items(Invoke(CaptureForPlan(new[]{p}),"BuildNormalEpgGroups",load,false));
    Check(groups.Count(g=>Get<IReadOnlyList<ChannelTarget>>(g,"Targets").Any(t=>t.OriginalNetworkId==4))==2,"direct alternative deduplicated against wrong driver");
});
Case("viewing reuses hybrid slot across waves but rejects foreign coordinates", () => {
    var p=Profile("view","BonDriver_A.dll",a,role:"Viewing"); var ctx=Context("viewer-reuse",p);
    var leases=new ExternalTunerLeaseService(ctx.Pool,new LogRepository(),new ApplicationOperationGate(new LogRepository()),ctx.Policy);
    ExternalTunerLeaseRequest Request(ushort nid, ushort tsid, ushort sid, string group, int space, int channel)
        => new() { ClientId="fixture", Source="test", Group=group, RequiredGroup=group, LogicalViewerSlotId=p.LogicalViewerSlotId,
            ViewerProfileId="slot-"+p.LogicalViewerSlotId, ViewerProfileName="Fixture", RequiredBonDriverFileName=p.BonDriverFileName,
            NetworkId=nid, TransportStreamId=tsid, ServiceId=sid, ChannelSpace=space, ChannelIndex=channel };
    var first=leases.Request(Request(0x7880,100,101,"GR",3,19)); Check(first.Success,"viewer GR acquisition");
    var second=leases.Request(Request(4,200,201,"BSCS",7,41)); Check(second.Success && second.Reused,"hybrid retune denied");
    var wrong=leases.Request(Request(4,200,201,"BSCS",12,90)); Check(!wrong.Success,"foreign retune coordinates accepted");
});
Case("physical EPG admission never counts one hybrid twice", () => {
    var profiles=new[]{Profile("H1","BonDriver_A.dll",a),Profile("view","BonDriver_B.dll",b,role:"Viewing")};
    var ctx=Context("epg-capacity",profiles);
    var count=typeof(EpgCapture).GetMethod("CountPhysicalEpgCapacity",BindingFlags.Static|BindingFlags.NonPublic)!;
    var groups=new[]{"GR","BSCS","HYBRID"};
    Check((int)count.Invoke(null,new object[]{ctx.Pool.GetStatus(),groups,true})! == 1,"hybrid EPG triple capacity");
    using var lease=ctx.Pool.AcquireForRecording("GR",1,at);
    Check((int)count.Invoke(null,new object[]{ctx.Pool.GetStatus(),groups,true})! == 0,"busy physical admission");
});
Case("duration uses physical lanes and deduplicates alternative routes", () => {
    object Estimate(TunerProfile[] profiles,string name) {
        var ctx=Context(name,profiles); var scheduler=RuntimeHelpers.GetUninitializedObject(typeof(EpgScheduler));
        foreach(var entry in new Dictionary<string,object>{{"tunerProfiles",profiles},{"channelLoader",ctx.Loader},{"capture",CaptureForPlan(profiles)},{"serviceAccess",ctx.Policy},{"log",new LogRepository()}})
            typeof(EpgScheduler).GetField(entry.Key,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(scheduler,entry.Value);
        return Invoke(scheduler,"EstimateEpgDuration","All","normal");
    }
    var one=Estimate(new[]{Profile("H1","BonDriver_A.dll",a)},"duration-one");
    var two=Estimate(new[]{Profile("H1","BonDriver_A.dll",a),Profile("H2","BonDriver_B.dll",b)},"duration-two");
    Check(Get<int>(one,"TsCount")==3 && Get<int>(two,"TsCount")==3,"alternative transports counted twice");
    Check(Get<int>(one,"TunerCount")==1 && Get<int>(one,"CaptureCriticalPathSeconds")>Get<int>(two,"CaptureCriticalPathSeconds"),"hybrid waves estimated in parallel on one tuner");
    Check(Get<string>(one,"Source")=="physical_tuner_route_duration_policy","duration unavailable");
});
Case("legacy EPG acquisition does not borrow explicit source tuner", () => {
    var profiles=new[]{Profile("H1","BonDriver_A.dll",a),Profile("S1","BonDriver_Legacy.dll","","BSCS")};
    var ctx=Context("legacy-acquire",profiles);
    using var lease=ctx.Pool.AcquireForEpg("BSCS",at,legacySourcesOnly:true);
    Check(lease is not null && lease.Name=="S1" && lease.ChannelCh2Path=="","legacy acquired foreign table");
});
Case("legacy PT3 coordinates coexist with direct BDA coordinates", () => {
    var profiles=new[]{Profile("H1","BonDriver_A.dll",a),Profile("S1","BonDriver_Legacy.dll","","BSCS")};
    var ctx=Context("mixed-legacy",profiles); var legacy=Ch2("legacy-s",Row("Legacy BS",0,1,4,200,201));
    var chset=Path.Combine(root,"legacy.ChSet.txt"); File.WriteAllText(chset,"$SPACE\nBS01\t0\t1\t0\t200\n");
    var loader=new ChannelFileLoader(Options.Create(new ChannelMapSettings{BscsChannelFilePath=legacy,BscsChSetFilePath=chset}),ctx.Ini);
    var access=new ChannelServiceAccessPolicy(loader).Capture();
    Check(access.TryResolveTargetForProfile(profiles[1],service,out var legacyTarget) && legacyTarget.ResolvedChannelIndex==1,"legacy ChSet broken");
    Check(access.TryResolveTargetForProfile(profiles[0],service,out var directTarget) && directTarget.ResolvedChannelIndex==41,"direct borrowed legacy route");
    using var lease=ctx.Pool.AcquireForRecordingByName("H1",1,at);
    Check(lease is not null && access.TryResolveDirectTargetForLease(lease,4,200,201,"BSCS",out var leased) && leased.ResolvedChannelIndex==41,"lease uses shared legacy route");
});
Case("explicit user chain retains one physical root tuner", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("chain",p);
    var parent=Reserve(ctx.Store,4,200,201,duration:30);
    var child=new Reservation { NetworkId=4,TransportStreamId=200,ServiceId=201,EventId=101,Source=ReservationSource.Manual,StartTime=at.AddMinutes(30),EndTime=at.AddMinutes(60),Title="Child",ServiceName="Fixture" };
    var mutation=ctx.Store.AddOrPromoteUserChain(child,parent.Id,parent.Id,true); Check(mutation.Applied,"chain creation: "+mutation.Reason);
    Evaluate(ctx.Store,ctx.Pool,new[]{p},chain:true,margin:10);
    var first=ctx.Store.GetById(parent.Id)!; var next=ctx.Store.GetById(mutation.ReservationId)!;
    Check(!first.IsConflicted && !next.IsConflicted && first.TunerName==p.Name && next.TunerName==p.Name,"chain root lost physical continuity");
});
foreach(var later in new[]{false,true})
Case("same-service adjacent priority contract " + later, () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("adjacent-"+later,p);
    var first=Reserve(ctx.Store,4,200,201,start:0,duration:30); var second=Reserve(ctx.Store,4,200,201,start:30,duration:30);
    Evaluate(ctx.Store,ctx.Pool,new[]{p},later:later,margin:10);
    Check(ctx.Store.GetById(first.Id)!.IsConflicted==later && ctx.Store.GetById(second.Id)!.IsConflicted!=later,"adjacent priority changed");
});
Case("active recording remains pinned against higher priority", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("active",p);
    var active=Reserve(ctx.Store,0x7880,100,101,ReservationSource.Keyword); var challenger=Reserve(ctx.Store,4,200,201);
    using(var con=ctx.Db.Open()) { using var cmd=con.CreateCommand(); cmd.CommandText="UPDATE reservations SET status='recording',tuner_name=$t,actual_tuner_name=$t WHERE id=$id"; cmd.Parameters.AddWithValue("$t",p.Name); cmd.Parameters.AddWithValue("$id",active.Id); cmd.ExecuteNonQuery(); }
    using var lease=ctx.Pool.AcquireForRecordingByName(p.Name,active.Id,active.EndTime);
    Evaluate(ctx.Store,ctx.Pool,new[]{p});
    Check(ctx.Store.GetById(active.Id)!.TunerName==p.Name && ctx.Store.GetById(challenger.Id)!.IsConflicted,"active recording displaced");
});
Case("EPG causal blocker is the occupied wave and generation", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("causal",p);
    ctx.Occupation.Set("GR",at.AddHours(1),at.AddHours(2),"unrelated",false,11);
    ctx.Occupation.Set("BSCS",at,at.AddMinutes(40),"actual",false,12);
    var r=Reserve(ctx.Store,0xfffe,300,301); Evaluate(ctx.Store,ctx.Pool,new[]{p});
    Check(ctx.Store.TryGetNormalEpgConflictBlockerEvidence(r.Id,out var evidence) && evidence.Group=="BSCS" && evidence.RunGeneration==12,"wrong EPG causal group");
});
Case("global disabled service cannot be reactivated by direct route", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("disabled",p); var load=ctx.Loader.Load();
    load.ServiceStates.Add(new ChannelServiceState{OriginalNetworkId=4,TransportStreamId=200,ServiceId=201,IsEnabled=false});
    var access=ctx.Policy.Capture();
    Check(!access.IsAvailableForDisplay(4,200,201) && !access.IsAvailableForRecording(4,200,201) && !access.TryResolveTargetForProfile(p,service,out _),"disabled service admitted");
});
Case("channel cache follows file and configured binding changes", () => {
    var file=Ch2("changing",Row("old",1,8,4,200,201)); var p=Profile("H1","BonDriver_A.dll",file); var ctx=Context("cache",p);
    var original=ctx.Policy.Capture(); Check(original.IsAvailableForRecording(4,200,201),"initial cache");
    File.WriteAllText(file,Row("new",3,44,4,200,202),Encoding.GetEncoding(932)); File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(2));
    var current=ctx.Policy.Capture(); Check(!current.IsAvailableForRecording(4,200,201) && current.IsAvailableForRecording(4,200,202),"stale file cache");
    Check(original.IsAvailableForRecording(4,200,201),"operation snapshot changed");
    ctx.Ini.Tuners[0].ChannelCh2Path=b;
    Check(ctx.Policy.Capture().TryResolveTargetForProfile(Profile("H1","BonDriver_A.dll",b),service,out var target) && target.ResolvedChannelIndex==90,"stale binding cache");
});
Case("ch2 name and tuning move keep the same service identity", () => {
    var file=Ch2("rename-move",Row("旧局名",1,8,4,200,201)); var p=Profile("H1","BonDriver_A.dll",file); var ctx=Context("rename-move",p);
    Check(ctx.Policy.Capture().TryResolveTargetForProfile(p,service,out var oldTarget) && oldTarget.ResolvedChannelIndex==8,"initial route");
    File.WriteAllText(file,Row("新局名",3,44,4,200,201),Encoding.GetEncoding(932)); File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(3));
    var current=ctx.Policy.Capture();
    Check(current.TryResolveTargetForProfile(p,service,out var target) && target.Name=="新局名" && target.ResolvedSpace==3 && target.ResolvedChannelIndex==44,"name or tuning coordinates remained stale");
    Check(current.DisplayTargets.Single(t=>t.ServiceId==201).Name=="新局名","display name remained stale");
});
Case("ch2 TSID change cannot silently retarget an old reservation identity", () => {
    var file=Ch2("tsid-move",Row("同じ局名",1,8,4,200,201)); var p=Profile("H1","BonDriver_A.dll",file); var ctx=Context("tsid-move",p);
    Check(ctx.Policy.Capture().IsAvailableForRecording(4,200,201),"initial identity");
    File.WriteAllText(file,Row("同じ局名",3,44,4,300,201),Encoding.GetEncoding(932)); File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(3));
    var current=ctx.Policy.Capture();
    Check(!current.TryResolveTargetForProfile(p,service,out _) && !current.IsAvailableForRecording(4,200,201),"old TSID guessed by station name");
    Check(current.TryResolveTargetForProfile(p,new BroadcastServiceKey(4,300,201),out var target) && target.ResolvedChannelIndex==44,"updated service unavailable");
});
void EnableAutomaticChannels(IniSettingsService ini)
{
    var dir=Path.Combine(root,"auto-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    foreach(var row in ini.Tuners)
    {
        if(!string.IsNullOrWhiteSpace(row.ChannelCh2Path)) File.Copy(row.ChannelCh2Path,Path.Combine(dir,Path.ChangeExtension(row.BonDriverFileName,".ch2")),true);
        row.ChannelCh2Path="";
    }
    foreach(var pair in new Dictionary<string,string>{{"TvTestExecutablePath",Path.Combine(dir,"TVTest.exe")},{"BonDriverDirectory",dir},{"GrChSetFilePath",""},{"BscsChSetFilePath",""}})
        typeof(IniSettingsService).GetProperty(pair.Key)!.SetValue(ini,pair.Value);
    Invoke(ini,"CapturePersistedTunerTopologyFromRuntime");
}
Case("automatic ch2 identifies the selected driver without ChSet or manual path",()=>{
    var ctx=Context("auto-inventory",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray();
    Check(ctx.Ini.Tuners.Single().ChannelCh2Path=="" && profiles.Single().ChannelCh2Path.EndsWith("BonDriver_A.ch2"),"manual path required or persisted");
    Check(ctx.Loader.Load().Targets.Count==0 && ctx.Loader.GetDisplayTargets().Count==3,"auto services hidden without ChSet");
    Check(ctx.Policy.Capture().TryResolveRecordingAdmission(4,200,201,ctx.Ini.GetEffectiveTuners(),out _,out _),"recording admission requires ChSet");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(profiles[0].ChannelCh2Path)!,"*ChSet*",SearchOption.AllDirectories).Any(),"fixture contains ChSet");
});
Case("automatic routes support manual keyword and program allocation",()=>{
    var ctx=Context("auto-reservation",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray(); var pool=new TunerPool(profiles,ctx.Ini,new LogRepository());
    foreach(var source in new[]{ReservationSource.Manual,ReservationSource.Keyword,ReservationSource.Program})
        Reserve(ctx.Store,4,200,201,source,start:(int)source*60);
    Evaluate(ctx.Store,pool,profiles);
    Check(ctx.Store.GetAll().All(r=>!r.IsConflicted&&!string.IsNullOrWhiteSpace(r.TunerName)),"auto source allocation failed");
    var resolved=Invoke(ctx.Store,"ResolveProgramRuleChannel",new ProgramRule{NetworkId=4,TransportStreamId=200,ServiceId=201});
    Check(Get<bool>(resolved,"IsResolved"),"program rule still needs legacy targets");
});
Case("automatic routes support normal EPG PreRec and recording worker target",()=>{
    var ctx=Context("auto-epg",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray(); var pool=new TunerPool(profiles,ctx.Ini,new LogRepository());
    var access=ctx.Policy.Capture(); var capture=CaptureForPlan(profiles);
    Check(Items(Invoke(capture,"BuildNormalEpgGroups",ctx.Loader.Load(),false)).Length==3,"normal EPG requires ChSet");
    using var lease=pool.AcquireForRecordingByName(profiles[0].Name,1,at);
    Check(lease is not null,"physical admission failed");
    Check(Items(Invoke(capture,"BuildPreRecordGroups",ctx.Loader.Load(),lease!,service)).Length==1,"PreRec requires ChSet");
    Check(access.TryResolveDirectTargetForLease(lease!,4,200,201,"BSCS",out var target) && target.ResolvedSpace==7 && target.ResolvedChannelIndex==41,"worker route requires ChSet");
});
Case("automatic routes support viewer preparation and actual viewer lease",()=>{
    var ctx=Context("auto-viewer",Profile("view","BonDriver_A.dll",a,role:"Viewing")); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray(); var p=profiles[0];
    Check(ctx.Policy.Capture().TryResolveTargetForProfile(p,service,out var target)&&target.ResolvedChannelIndex==41,"viewer preparation failed");
    var leases=new ExternalTunerLeaseService(new TunerPool(profiles,ctx.Ini,new LogRepository()),new LogRepository(),new ApplicationOperationGate(new LogRepository()),ctx.Policy);
    var request=new ExternalTunerLeaseRequest{ClientId="fixture",Source="test",Group="BSCS",RequiredGroup="BSCS",LogicalViewerSlotId=p.LogicalViewerSlotId,ViewerProfileId="slot-"+p.LogicalViewerSlotId,ViewerProfileName="Fixture",RequiredBonDriverFileName=p.BonDriverFileName,NetworkId=4,TransportStreamId=200,ServiceId=201,ChannelSpace=7,ChannelIndex=41};
    var result=leases.Request(request); Check(result.Success,"viewer lease failed: "+result.Reason);
});
Case("automatic ch2 rejects missing selected driver and honors folder precedence",()=>{
    var dir=Path.Combine(root,"resolve"); var drivers=Path.Combine(dir,"drivers"); Directory.CreateDirectory(drivers);
    File.Copy(a,Path.Combine(dir,"BonDriver_A.ch2")); File.Copy(b,Path.Combine(drivers,"BonDriver_A.ch2"));
    var exe=Path.Combine(dir,"TVTest.exe");
    Check(BonDriverChannelSourceResolver.Resolve(exe,drivers,"BonDriver_A.dll")==Path.Combine(drivers,"BonDriver_A.ch2"),"folder precedence");
    Check(BonDriverChannelSourceResolver.Resolve(exe,drivers,"BonDriver_Missing.dll").EndsWith("BonDriver_Missing.ch2"),"borrowed foreign source");
    var ctx=Context("auto-missing",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    ctx.Ini.Tuners[0].BonDriverFileName="BonDriver_Missing.dll";
    Check(!ctx.Policy.Capture().IsAvailableForRecording(4,200,201),"missing driver used another driver's channels");
});
Case("automatic route keeps a user chain on one physical tuner",()=>{
    var ctx=Context("auto-chain",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray(); var pool=new TunerPool(profiles,ctx.Ini,new LogRepository());
    var parent=Reserve(ctx.Store,4,200,201);
    var child=new Reservation{NetworkId=4,TransportStreamId=200,ServiceId=201,EventId=101,Source=ReservationSource.Manual,StartTime=at.AddMinutes(30),EndTime=at.AddMinutes(60),Title="Child"};
    var mutation=ctx.Store.AddOrPromoteUserChain(child,parent.Id,parent.Id,true);
    Check(mutation.Applied,"auto chain creation"); Evaluate(ctx.Store,pool,profiles,chain:true,margin:10);
    Check(ctx.Store.GetById(parent.Id)!.TunerName==profiles[0].Name && ctx.Store.GetById(mutation.ReservationId)!.TunerName==profiles[0].Name,"auto chain split physical tuner");
});
Case("automatic route reloads updated ch2 for the actual recording lease",()=>{
    var ctx=Context("auto-reload",Profile("H1","BonDriver_A.dll",a)); EnableAutomaticChannels(ctx.Ini);
    var profiles=TunerRuntimeProfileSource.Build(ctx.Ini,null).ToArray(); var pool=new TunerPool(profiles,ctx.Ini,new LogRepository());
    using var lease=pool.AcquireForRecordingByName(profiles[0].Name,1,at);
    Check(ctx.Policy.Capture().TryResolveDirectTargetForLease(lease!,4,200,201,"BSCS",out var initial)&&initial.ResolvedChannelIndex==41,"initial auto route");
    File.WriteAllText(profiles[0].ChannelCh2Path,Row("新局名",12,52,4,200,201),Encoding.GetEncoding(932)); File.SetLastWriteTimeUtc(profiles[0].ChannelCh2Path,DateTime.UtcNow.AddSeconds(4));
    Check(ctx.Policy.Capture().TryResolveDirectTargetForLease(lease!,4,200,201,"BSCS",out var updated)&&updated.ResolvedChannelIndex==52&&updated.Name=="新局名","auto route or station name stayed stale");
});
Case("ChSet optional mode persists and changes runtime only after restart",()=>{
    var ctx=Context("auto-mode",Profile("T1","BonDriver_A.dll","","GR")); EnableAutomaticChannels(ctx.Ini);
    var dto=System.Text.Json.JsonSerializer.Deserialize<IniSettingsUpdateDto>(System.Text.Json.JsonSerializer.Serialize(ctx.Ini.ToDto()))!;
    dto.GrChSetFilePath=Path.Combine(root,"optional.ChSet.txt");
    Check(!ctx.Ini.MatchesRuntimeTunerTopology(dto),"mode change not classified as restart");
    Invoke(ctx.Ini,"SaveFromApplicationService",dto,false,false);
    Check(ctx.Ini.GrChSetFilePath=="" && ctx.Ini.ToDto().GrChSetFilePath==dto.GrChSetFilePath && ctx.Ini.IsTunerTopologyRestartPending(),"live mode changed before restart");
    var loaded=new IniSettingsService(Path.Combine(root,"auto-mode"),Path.Combine(root,"auto-mode","data"),18787);
    Check(loaded.GrChSetFilePath==dto.GrChSetFilePath,"mode not persisted");
    var clear=System.Text.Json.JsonSerializer.Deserialize<IniSettingsUpdateDto>(System.Text.Json.JsonSerializer.Serialize(loaded.ToDto()))!; clear.GrChSetFilePath="";
    Invoke(loaded,"SaveFromApplicationService",clear,false,false);
    var cleared=new IniSettingsService(Path.Combine(root,"auto-mode"),Path.Combine(root,"auto-mode","data"),18787);
    Check(cleared.GrChSetFilePath=="" && cleared.GetEffectiveTuners()[0].ChannelCh2Path.EndsWith("BonDriver_A.ch2"),"empty ChSet lost after restart");
});
Case("individual channel path persists while runtime topology waits for restart", () => {
    var p=Profile("H1","BonDriver_A.dll",a); var ctx=Context("ini-path",p);
    var dto=System.Text.Json.JsonSerializer.Deserialize<IniSettingsUpdateDto>(System.Text.Json.JsonSerializer.Serialize(ctx.Ini.ToDto()))!;
    dto.Tuners=new List<TunerProfileDto>{Dto(Profile("H1","BonDriver_A.dll",b))};
    Invoke(ctx.Ini,"SaveFromApplicationService",dto,false,false);
    Check(ctx.Ini.Tuners.Single().ChannelCh2Path==a,"saved path changed live topology");
    Check(ctx.Ini.ToDto().Tuners.Single().ChannelCh2Path==b,"pending path missing from settings");
    var reloaded=new IniSettingsService(Path.Combine(root,"ini-path"),Path.Combine(root,"ini-path","data"),18787);
    Check(reloaded.Tuners.Single().ChannelCh2Path==b,"path was lost after restart");
});
Case("viewer preparation selects the requested physical profile table", () => {
    var p=Profile("view","BonDriver_B.dll",b,role:"Viewing"); p.DeviceNumber=1;
    var ctx=Context("viewer-prepare",p); ctx.Ini.Tuners[0].DeviceNumber=1;
    var log=new LogRepository(); var appGate=new ApplicationOperationGate(log);
    using var hub=new PluginTypedEventHub(log,new ReservationMutationJournal(),ctx.Loader);
    var leases=new ExternalTunerLeaseService(ctx.Pool,log,appGate,ctx.Policy);
    var operations=new ViewerOperationService(ctx.Policy,leases,ctx.Pool,new ViewerSessionRegistry(log,hub),null!,Options.Create(new TvTestSettings{ExecutablePath=Path.Combine(root,"TVTest.exe")}),ctx.Ini,new[]{p},log,appGate);
    var prepared=operations.Prepare(new ViewerOperationPreparationRequest("test","start","viewer-slot-view",null,null,4,200,201));
    Check(prepared.Success && prepared.Plan!.Channel.ResolvedSpace==12 && prepared.Plan.Channel.ResolvedChannelIndex==90,"viewer did not use selected driver source: "+prepared.ErrorCode);
    var rejected=operations.Prepare(new ViewerOperationPreparationRequest("test","start","viewer-slot-view",null,null,0x7880,100,101));
    Check(!rejected.Success,"viewer borrowed recording/other driver route");
});
Case("early-end evidence retains actual present freshness and event identity", () => {
    var scheduler=typeof(ReservationStore).Assembly.GetType("TvAIr.Schedule.ReservationScheduler")!;
    var evidence=scheduler.GetNestedType("LiveRecordingEventEvidence",BindingFlags.NonPublic)!;
    var now=DateTime.Now; var reservation=new Reservation { EventId=100 };
    object Make(int eid=100,int section=0,int age=0) => Activator.CreateInstance(evidence,new object[]{now.AddMinutes(-10),now.AddMinutes(10),now,4,0x4e,0,1,eid,now.AddMinutes(-10),now.AddMinutes(10),now.AddSeconds(-age),4,0x4e,section,1})!;
    Check((bool)Invoke(Make(),"HasFreshExpectedEvent",now) && (bool)Invoke(Make(),"IsFreshTargetPresent",reservation,now),"fresh event rejected");
    Check(!(bool)Invoke(Make(eid:101),"IsFreshTargetPresent",reservation,now),"other EID admitted");
    Check(!(bool)Invoke(Make(section:1),"IsFreshTargetPresent",reservation,now),"following section admitted");
    Check(!(bool)Invoke(Make(age:31),"IsFreshTargetPresent",reservation,now),"stale evidence admitted");
    var original=new EpgEvent { NetworkId=4,TransportStreamId=200,ServiceId=201,EventId=100,Start=now.AddMinutes(-10),End=now.AddMinutes(20),TableId=0x4e,SectionNumber=0,RawDescriptorLoopHex="abc",Title="original" };
    var clone=(EpgEvent)scheduler.GetMethod("CloneEpgEventWithEnd",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{original,now})!;
    Check(clone.End==now && clone.DurationSeconds==600 && clone.EventId==100 && clone.RawDescriptorLoopHex=="abc" && original.End!=clone.End,"early-end clone lost identity or mutated store");
});
File.WriteAllText(Path.Combine(root,"results.json"),System.Text.Json.JsonSerializer.Serialize(new{total=results.Count,failed,cases=results},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failed}/{results.Count} passed; synthetic inputs only; no RF verification. Results: {root}");
return failed==0 ? 0 : 1;
