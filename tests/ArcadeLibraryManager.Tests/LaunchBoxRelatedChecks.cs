using ArcadeLibraryManager.Core;
using System.Text.Json;
using System.Xml.Linq;

public static class LaunchBoxRelatedChecks
{
    static void Check(bool ok,string message){if(!ok)throw new Exception("Playlist integration: "+message);}
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var tp=Path.Combine(root,"tp");var lb=Path.Combine(root,"lb");
        foreach(var d in new[]{Path.Combine(tp,"UserProfiles"),Path.Combine(tp,"GameProfiles"),Path.Combine(tp,"Metadata"),Path.Combine(lb,"Data","Platforms"),Path.Combine(lb,"Data","Playlists")})Directory.CreateDirectory(d);
        var settings=new AppSettings{TeknoParrotPath=tp,LaunchBoxPath=lb,FillMissingMetadata=false};
        GameRecord Profile(string id,string name,string emulator="Test")
        {
            var launch=Path.Combine(root,id+".exe");File.WriteAllText(launch,"fixture");var path=Path.Combine(tp,"UserProfiles",id+".xml");
            var doc=new XDocument(new XElement("GameProfile",new XElement("GamePath",launch),new XElement("EmulatorType",emulator)));
            doc.Save(path);doc.Save(Path.Combine(tp,"GameProfiles",id+".xml"));File.WriteAllText(Path.Combine(tp,"Metadata",id+".json"),JsonSerializer.Serialize(new{game_name=name}));
            return new(){Id=id,Name=name,UserProfilePath=path,Installed=true,PathsValid=true,Emulator=emulator};
        }
        XElement Game(string id,GameRecord profile)=>new("Game",new XElement("ID",id),new XElement("Title",profile.Name),new XElement("ApplicationPath",Path.GetRelativePath(lb,profile.UserProfilePath)),new XElement("Emulator","tp"),new XElement("PlayCount",0),new XElement("PlayTime",0),new XElement("DatabaseID",123));
        XElement Member(string id,string title,int order)=>new("PlaylistGame",new XElement("GameId",id),new XElement("LaunchBoxDbId",123),new XElement("GameTitle",title),new XElement("GameFileName","old.xml"),new XElement("GamePlatform","TeknoParrot"),new XElement("ManualOrder",order));
        var legacy=Profile("Spicy","Too Spicy","Lindbergh");var elf=Profile("SpicyElf2","Too Spicy (ElfLoader 2)","ElfLdr2");
        var usa=Profile("ViceUsa","Total Vice (USA)");var world=Profile("ViceWorld","Total Vice (World)");
        var platform=Path.Combine(lb,"Data","Platforms","TeknoParrot.xml");var playlist=Path.Combine(lb,"Data","Playlists","Light Gun Games.xml");
        new XDocument(new XElement("LaunchBox",new XElement("Emulator",new XElement("ID","tp"),new XElement("ApplicationPath",Path.Combine(tp,"TeknoParrotUi.exe")),new XElement("CommandLine","--profile=%romfile%")))).Save(Path.Combine(lb,"Data","Emulators.xml"));
        Directory.CreateDirectory(tp); File.WriteAllText(Path.Combine(tp, "TeknoParrotUi.exe"), "inert executable fixture");
        void Reset()
        {
            new XDocument(new XElement("LaunchBox",Game("spicy-old",legacy),Game("spicy-new",elf),Game("vice-usa",usa),Game("vice-world",world))).Save(platform);
            new XDocument(new XElement("LaunchBox",new XElement("Playlist",new XElement("Name","Light Gun Games"),new XElement("Custom","Keep playlist config")),Member("spicy-old","Too Spicy",0),Member("spicy-new","Too Spicy (ElfLoader 2)",143),Member("vice-usa","Total Vice (USA)",7),Member("vice-world","Total Vice (World)",8),Member("unrelated-mame","Total Vice",144))).Save(playlist);
        }
        Reset();var service=new LaunchBoxService(()=>false,VolumeHealthChecks.CleanProvider());
        var beforePlatform=File.ReadAllBytes(platform);var beforePlaylist=File.ReadAllBytes(playlist);
        var plan=await service.PreviewAsync(settings,[legacy,elf,usa,world]);
        Check(plan.Items.Single(i=>i.ProfileId==elf.Id).Action=="Combine","referenced loader pair remains combinable");
        Check(plan.Items.Single(i=>i.ProfileId==usa.Id).Action=="Consolidate","regional duplicates produce preferred-only consolidation");
        Check(File.ReadAllBytes(platform).SequenceEqual(beforePlatform)&&File.ReadAllBytes(playlist).SequenceEqual(beforePlaylist),"preview is read-only");
        plan.Items.Single(i=>i.ProfileId==usa.Id).Selected=false;
        var applied=await service.ApplyAsync(plan);Check(applied.Succeeded==1,"only checked loader group commits");
        var saved=XDocument.Load(playlist);var members=saved.Root!.Elements("PlaylistGame").ToList();
        Check(members.Count==4&&members.Count(e=>(string?)e.Element("GameId")=="spicy-old")==1&&!members.Any(e=>(string?)e.Element("GameId")=="spicy-new"),"loader references collapse to one playlist membership");
        Check(members.Any(e=>(string?)e.Element("GameId")=="vice-world"),"deselected regional cleanup does not alter its playlist membership");
        var spicy=members.Single(e=>(string?)e.Element("GameId")=="spicy-old");Check((string?)spicy.Element("GameFileName")=="SpicyElf2.xml"&&(string?)spicy.Element("ManualOrder")=="0","surviving playlist position retained and launch filename updated");
        Check(saved.Root.Element("Playlist")?.Element("Custom")?.Value=="Keep playlist config"&&members.Any(e=>(string?)e.Element("GameId")=="unrelated-mame"),"custom config and unrelated same-title MAME game stay untouched");
        await service.RollbackAsync(Path.Combine(Path.GetDirectoryName(plan.BackupPath)!,"rollback.json"));
        Check(File.ReadAllBytes(platform).SequenceEqual(beforePlatform)&&File.ReadAllBytes(playlist).SequenceEqual(beforePlaylist),"undo restores platform and playlist exact bytes");
        var all=await service.PreviewAsync(settings,[legacy,elf,usa,world]);Check((await service.ApplyAsync(all)).Succeeded==2,"both reviewed groups commit together");
        Check(XDocument.Load(platform).Root!.Elements("Game").Count()==2&&XDocument.Load(playlist).Root!.Elements("PlaylistGame").Count()==3,"one preferred game per family and one playlist membership per surviving game");
        Check(!(await service.PreviewAsync(settings,[legacy,elf,usa,world])).CanApply,"playlist-linked consolidation is repeatable without new writes");
        Reset();File.WriteAllText(playlist,File.ReadAllText(playlist).Replace("<GameId>spicy-new</GameId>","<GameId>&#x73;picy-new</GameId>"));
        var encoded=await service.PreviewAsync(settings,[elf]);Check(encoded.Items.Single().Action=="Combine","encoded game IDs must participate in reference matching");await service.ApplyAsync(encoded);
        Check(!XDocument.Load(playlist).Descendants("GameId").Any(e=>e.Value=="spicy-new"),"encoded membership cannot be left dangling");
        Reset();var casing=XDocument.Load(playlist);var newMembership=casing.Root!.Elements("PlaylistGame").Single(e=>(string?)e.Element("GameId")=="spicy-new");newMembership.Remove();casing.Root.AddFirst(newMembership);
        casing.Root.Elements("PlaylistGame").Single(e=>(string?)e.Element("GameId")=="spicy-old").Element("GameId")!.Name="GameID";casing.Save(playlist);
        var cased=await service.PreviewAsync(settings,[elf]);Check(cased.Items.Single().Action=="Combine","accepted GameID casing cannot create a false custom-field conflict");await service.ApplyAsync(cased);
        var preserved=XDocument.Load(playlist).Root!.Elements("PlaylistGame").Single(e=>e.Elements().Any(f=>f.Name.LocalName.Equals("GameId",StringComparison.OrdinalIgnoreCase)&&f.Value=="spicy-old"));
        Check(preserved.Element("ManualOrder")?.Value=="0","surviving membership keeps its position even when a remapped duplicate comes first and uses different ID casing");
        Reset();var custom=XDocument.Load(playlist);custom.Root!.Elements("PlaylistGame").Single(e=>(string?)e.Element("GameId")=="spicy-new").Add(new XElement("UserSpecific","Do not discard"));custom.Save(playlist);
        var conflict=await service.PreviewAsync(settings,[elf]);Check(conflict.Items.Single().Action=="NeedsReview","conflicting unknown membership metadata cannot be silently dropped");
        Reset();var unknown=Path.Combine(lb,"Data","ExternalLinks.xml");File.WriteAllText(unknown,"<LaunchBox><CustomLink>&#x73;picy-new</CustomLink></LaunchBox>");
        var blocked=await service.PreviewAsync(settings,[elf]);Check(blocked.Items.Single().Action=="NeedsReview","unsupported external game link blocks removal");File.Delete(unknown);
        var stale=await service.PreviewAsync(settings,[elf]);File.WriteAllText(unknown,"<LaunchBox />");var initial=File.ReadAllBytes(platform);bool refused=false;try{await service.ApplyAsync(stale);}catch(InvalidOperationException){refused=true;}
        Check(refused&&File.ReadAllBytes(platform).SequenceEqual(initial),"new external data files require a fresh preview before any write");
    }
}
