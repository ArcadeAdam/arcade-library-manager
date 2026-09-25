using ArcadeLibraryManager.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

public static class InstallSelectionChecks
{
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    public static async Task RunAsync(string root)
    {
        var all = Create(Path.Combine(root,"all"));
        var engine = new AppEngine(all.Settings,all.Data,volumeHealth:VolumeHealthChecks.CleanProvider());
        await engine.IndexRomsAsync(default);
        var plan = await engine.PlanAsync(all.Ids,default);
        Check(plan.Count==4 && plan.Single(p=>p.Selected).ProfileId=="vice-usa","USA must be the only installable regional version");
        Check(plan.Count(p=>p.Action=="Alternate version")==3,"Suppressed variants must explain their exclusion");
        foreach(var item in plan)item.Selected=true;
        var result = await engine.ExecuteAsync(plan,default);
        Check(result.Succeeded==1 && result.Skipped==3 && result.Errors.Count==0,"Re-enabling alternate rows cannot install them");
        Check(Directory.GetFiles(Path.Combine(all.Settings.TeknoParrotPath,"UserProfiles"),"*.xml").Length==1,"Only one TeknoParrot profile may be added");

        var fallback = Create(Path.Combine(root,"fallback"),includeUsaRecipe:false);
        var fallbackEngine = new AppEngine(fallback.Settings,fallback.Data,volumeHealth:VolumeHealthChecks.CleanProvider());
        await fallbackEngine.IndexRomsAsync(default);
        var fallbackPlan = await fallbackEngine.PlanAsync(fallback.Ids,default);
        Check(fallbackPlan.Single(p=>p.Selected).ProfileId=="vice-world","Missing USA recipe must fall back to available World");
        Check((await fallbackEngine.ExecuteAsync(fallbackPlan,default)).Succeeded==1,"World fallback must install");
        var existingWorldHash=SafeFiles.Hash(Path.Combine(fallback.Settings.TeknoParrotPath,"UserProfiles","vice-world.xml"));
        var repeated = await fallbackEngine.PlanAsync(["vice-japan"],default);
        Check(repeated.Single().Action=="Alternate version" && !repeated.Single().Selected,"A subset selection cannot create a second installed regional profile");
        repeated.Single().Selected=true;
        Check((await fallbackEngine.ExecuteAsync(repeated,default)).Succeeded==0,"Execution must enforce one version across separate runs");
        Check(SafeFiles.Hash(Path.Combine(fallback.Settings.TeknoParrotPath,"UserProfiles","vice-world.xml"))==existingWorldHash,"Existing World controls and profile must remain untouched");

        var old = Create(Path.Combine(root,"old-checkpoint"));
        var oldEngine = new AppEngine(old.Settings,old.Data,volumeHealth:VolumeHealthChecks.CleanProvider());
        await oldEngine.IndexRomsAsync(default);
        var oldPlans = new List<InstallPlanItem>();
        foreach(var id in old.Ids)oldPlans.Add((await oldEngine.PlanAsync([id],default)).Single());
        var checkpointStore=new JobStore(old.Data);
        foreach(var item in oldPlans)checkpointStore.Set(item.ProfileId,item.Name,"Paused","Older checkpoint fixture",item);
        var checkpoint=await oldEngine.ReviewPendingPlanAsync(default);
        Check(checkpoint.Single(p=>p.Selected).ProfileId=="vice-usa" && checkpoint.Count(p=>p.Action=="Alternate version")==3,"Resumed checkpoint preview must only check the preferred version");
        Check(checkpoint.Single(p=>p.Selected).ArchivePath==oldPlans.Single(p=>p.ProfileId=="vice-usa").ArchivePath,"Resume review must retain original archive and checkpoint fields");
        var replay = await oldEngine.ExecuteAsync(oldPlans,default);
        Check(replay.Succeeded==1 && replay.Skipped==3,"An older multi-locale retry checkpoint must be reduced at execution");

        var workflow = Create(Path.Combine(root,"workflow"));
        foreach(var id in workflow.Ids)
        {
            var launch=Path.Combine(workflow.Settings.TeknoParrotPath,id+".exe");File.WriteAllText(launch,"fixture");
            var profile=XDocument.Load(Path.Combine(workflow.Settings.TeknoParrotPath,"GameProfiles",id+".xml"));
            profile.Root!.SetElementValue("GamePath",launch);profile.Save(Path.Combine(workflow.Settings.TeknoParrotPath,"UserProfiles",id+".xml"));
        }
        var lb=workflow.Settings.LaunchBoxPath;
        Directory.CreateDirectory(Path.Combine(lb,"Data","Platforms"));
        var platform=Path.Combine(lb,"Data","Platforms","TeknoParrot.xml");File.WriteAllText(platform,"<LaunchBox />");
        new XDocument(new XElement("LaunchBox",new XElement("Emulator",new XElement("ID","tp"),new XElement("ApplicationPath",Path.Combine(workflow.Settings.TeknoParrotPath,"TeknoParrotUi.exe")),new XElement("CommandLine","--profile=%romfile%")))).Save(Path.Combine(lb,"Data","Emulators.xml"));
        Directory.CreateDirectory(workflow.Settings.TeknoParrotPath); File.WriteAllText(Path.Combine(workflow.Settings.TeknoParrotPath, "TeknoParrotUi.exe"), "inert executable fixture");
        var service=new LibraryWorkflowService(workflow.Settings,workflow.Data,health:VolumeHealthChecks.CleanProvider(),launchBoxRunning:()=>false);
        var review=await service.PreviewAsync(workflow.Ids,new(){SyncLaunchBox=true});
        Check(review.Rows.Count(r=>r.Selected)==1 && review.Rows.Single(r=>r.Selected).ProfileId=="vice-usa","Workflow selects exactly USA and retains excluded rows for review");
        var excluded=review.Rows.First(r=>r.ExcludedByVersionPolicy);excluded.Selected=true;
        var before=SafeFiles.Hash(platform);bool rejected=false;
        try{await service.ExecuteAsync(review,workflow.Settings);}catch(InvalidOperationException){rejected=true;}
        Check(rejected && SafeFiles.Hash(platform)==before,"Workflow must reject manually re-enabled alternates before writing");
        excluded.Selected=false;
        var run=await service.ExecuteAsync(review,workflow.Settings);
        Check(run.CompletedGames==1 && run.Errors.Count==0,"Preferred workflow version must complete");
        Check(XDocument.Load(platform).Root!.Elements("Game").Single().Element("Title")?.Value=="Total Vice (USA)","Workflow may add only preferred USA game");
        await LoaderWorkflow(Path.Combine(root,"loader-workflow"));
    }
    private static async Task LoaderWorkflow(string root)
    {
        var fixture=Create(root);var tp=fixture.Settings.TeknoParrotPath;var lb=fixture.Settings.LaunchBoxPath;
        fixture.Settings.FillMissingMetadata=false;
        foreach(var (id,title,emulator) in new[]{("vice-japan","Example Voyager","Lindbergh"),("vice-usa","Example Voyager (ElfLoader 2)","ElfLdr2")})
        {
            var launch=Path.Combine(tp,id+".exe");File.WriteAllText(launch,"fixture");
            var profile=new XDocument(new XElement("GameProfile",new XElement("GamePath",launch),new XElement("EmulatorType",emulator),new XElement("Controls","Keep controls")));
            profile.Save(Path.Combine(tp,"GameProfiles",id+".xml"));profile.Save(Path.Combine(tp,"UserProfiles",id+".xml"));
            File.WriteAllText(Path.Combine(tp,"Metadata",id+".json"),JsonSerializer.Serialize(new{game_name=title}));
        }
        Directory.CreateDirectory(Path.Combine(lb,"Data","Platforms"));
        var platform=Path.Combine(lb,"Data","Platforms","TeknoParrot.xml");
        XElement Game(string id,string profile,string title)=>new("Game",new XElement("ID",id),new XElement("Title",title),new XElement("ApplicationPath",Path.GetRelativePath(lb,Path.Combine(tp,"UserProfiles",profile+".xml"))),new XElement("Emulator","tp"),new XElement("CommandLine",""),new XElement("PlayCount",0),new XElement("PlayTime",0));
        new XDocument(new XElement("LaunchBox",Game("parent-old","vice-japan","Example Voyager"),Game("parent-new","vice-usa","Example Voyager (ElfLoader 2)"))).Save(platform);
        new XDocument(new XElement("LaunchBox",new XElement("Emulator",new XElement("ID","tp"),new XElement("ApplicationPath",Path.Combine(tp,"TeknoParrotUi.exe")),new XElement("CommandLine","--profile=%romfile%")))).Save(Path.Combine(lb,"Data","Emulators.xml"));
        Directory.CreateDirectory(tp); File.WriteAllText(Path.Combine(tp, "TeknoParrotUi.exe"), "inert executable fixture");
        var service=new LibraryWorkflowService(fixture.Settings,fixture.Data,health:VolumeHealthChecks.CleanProvider(),launchBoxRunning:()=>false);
        var review=await service.PreviewAsync(["vice-japan","vice-usa"],new(){SyncLaunchBox=true});
        Check(review.Rows.Single(r=>r.Selected).StagePlans.Single().Action=="Combine","Workflow must review ELF2 combination as its one selected game");
        var result=await service.ExecuteAsync(review,fixture.Settings);
        Check(result.CompletedGames==1 && result.Errors.Count==0,"Workflow must execute the new Combine action");
        var document=XDocument.Load(platform);var primary=document.Root!.Elements("Game").Single();
        Check(primary.Element("ApplicationPath")!.Value.EndsWith("vice-usa.xml"),"Combined workflow defaults to ELF2");
        Check(document.Root.Elements("AdditionalApplication").Any(e=>e.Element("ApplicationPath")?.Value.EndsWith("vice-japan.xml")==true),"Combined workflow retains the original loader as an alternate");
        var repeat=await service.PreviewAsync(["vice-japan","vice-usa"],new(){SyncLaunchBox=true});
        Check((await service.ExecuteAsync(repeat,fixture.Settings)).UnchangedStages==1,"Repeated combined workflow must be idempotent");
    }
    private static (AppSettings Settings,string Data,string[] Ids) Create(string root,bool includeUsaRecipe=true)
    {
        Directory.CreateDirectory(root);var tp=Path.Combine(root,"tp");var data=Path.Combine(root,"data");var roms=Path.Combine(root,"roms");
        foreach(var directory in new[]{"GameProfiles","UserProfiles","Metadata"})Directory.CreateDirectory(Path.Combine(tp,directory));
        Directory.CreateDirectory(data);Directory.CreateDirectory(roms);
        var recipes=new List<GameRecipe>();var ids=new List<string>();
        foreach(var region in new[]{"Japan","Europe","World","USA"})
        {
            var id="vice-"+region.ToLowerInvariant();ids.Add(id);
            File.WriteAllText(Path.Combine(tp,"GameProfiles",id+".xml"),"<GameProfile><GamePath /><ExecutableName>game.exe</ExecutableName><Controls>Keep controls</Controls></GameProfile>");
            File.WriteAllText(Path.Combine(tp,"Metadata",id+".json"),JsonSerializer.Serialize(new{game_name="Total Vice ("+region+")"}));
            var archive=Path.Combine(roms,id+".zip");
            using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))using(var writer=new StreamWriter(zip.CreateEntry("game.exe").Open()))writer.Write("fixture "+id);
            if(region=="USA"&&!includeUsaRecipe)continue;
            using var stream=File.OpenRead(archive);
            recipes.Add(new(){Id=id,Name="Total Vice ("+region+")",PayloadId=id,ArchiveName=Path.GetFileName(archive),ArchiveSize=stream.Length,ArchiveSha1=Convert.ToHexString(SHA1.HashData(stream)),PrimaryPath="game.exe"});
        }
        File.WriteAllText(Path.Combine(data,"recipes.json"),JsonSerializer.Serialize(recipes));
        return (new(){TeknoParrotPath=tp,DestinationPath=Path.Combine(root,"installed"),RomRoots=[roms],LaunchBoxPath=Path.Combine(root,"lb")},data,ids.ToArray());
    }
}
