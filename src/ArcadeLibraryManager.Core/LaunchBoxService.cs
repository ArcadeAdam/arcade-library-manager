using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed class LaunchBoxIdentity {
 public string ProfileId { get; init; } = "";
 public string GameGuid { get; init; } = "";
 public int? DatabaseId { get; init; }
 public string Title { get; init; } = "";
 public string ApplicationPath { get; init; } = "";
 public bool IsAdditionalApplication { get; init; }
 public bool Duplicate { get; init; }
}
public sealed class LaunchBoxPlanItem {
 public bool Selected { get; set; } = true;
 public string ProfileId { get; init; } = "";
 public string Name { get; init; } = "";
 public string Action { get; internal set; } = "";
 public string Detail { get; internal set; } = "";
 public string GameGuid { get; internal set; } = "";
 public int? DatabaseId { get; internal set; }
 internal string ElementName { get; set; } = "Game";
 internal Dictionary<string,string> Changes { get; } = new(StringComparer.Ordinal);
 internal XElement? NewGame { get; set; }
 internal string ProfilePath { get; set; } = "";
 internal string ProfileHash { get; set; } = "";
 internal List<string> RemovedGameIds { get; } = new();
 internal List<string> RemovedApplicationIds { get; } = new();
 internal List<XElement> AddedApplications { get; } = new();
}
public sealed class LaunchBoxPlan {
 public string PlatformName { get; internal set; } = "";
 public string SourcePath { get; internal set; } = "";
 public string BackupPath { get; internal set; } = "";
 public List<LaunchBoxPlanItem> Items { get; } = new();
 public Dictionary<string,string> MediaFolders { get; internal set; } = new(StringComparer.OrdinalIgnoreCase);
 public bool CanApply => Items.Any(i => i.Selected && i.Action is "Add" or "Update" or "Combine" or "Consolidate");
 internal string TeknoParrotPath { get; set; } = "";
 internal Dictionary<string,string> SourceHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
 internal XDocument Document { get; set; } = new();
 internal Dictionary<string,XDocument> RelatedDocuments { get; } = new(StringComparer.OrdinalIgnoreCase);
 internal Dictionary<string,string> GameIdRemaps { get; } = new(StringComparer.OrdinalIgnoreCase);
 internal List<string>? ExternalDataInventory { get; set; }
}
public sealed record LaunchBoxRollback(string SourcePath,string BackupPath,string BeforeHash,string AfterHash,DateTimeOffset CommittedUtc) {
 public List<LaunchBoxRollbackFile> Files { get; init; } = new();
 public string State { get; init; } = "Committed";
}

/// <summary>Conservative, exact-profile XML integration. Preview is read-only; commit preserves every unrelated XML node.</summary>
public sealed partial class LaunchBoxService {
 private static readonly SemaphoreSlim CommitGate = new(1,1);
 private readonly Func<bool> isRunning;
 private readonly VolumeHealthService volumeHealth;
 public LaunchBoxService(Func<bool>? processRunning = null,VolumeHealthService? healthService=null) {isRunning=processRunning??IsLaunchBoxRunning;volumeHealth=healthService??new();}
 public static bool IsLaunchBoxRunning() => new[]{"LaunchBox","BigBox"}.Any(n => { foreach(var p in Process.GetProcessesByName(n)) { p.Dispose(); return true; } return false; });
 internal static XDocument ReadXml(string path) { using var r=XmlReader.Create(path,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}); return XDocument.Load(r,LoadOptions.PreserveWhitespace); }
 internal static string Value(XElement? e,string key) => e?.Element(key)?.Value ?? "";
 internal static string Hash(string p) { using var stream=File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(stream)); }
 internal static string FullPath(string root,string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root,path));
 internal static string PlatformFile(AppSettings s) {
  if(string.IsNullOrWhiteSpace(s.PlatformName) || s.PlatformName.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || s.PlatformName is "." or "..") throw new InvalidDataException("Choose an existing LaunchBox platform name.");
  return Path.Combine(s.LaunchBoxPath,"Data","Platforms",s.PlatformName+".xml");
 }
 internal static string? ProfileId(XElement e,string lbRoot,string tpRoot,ISet<string>? known=null) {
  var p=Value(e,"ApplicationPath").Trim('"');
  if(p.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)) {
   var id=Path.GetFileNameWithoutExtension(p.Replace('\\','/'));
   if(known!=null && !known.Contains(id)) return null;
   // UserProfiles is an explicit profile identity; this also permits repair after moving TeknoParrot.
   if(p.Replace('\\','/').Contains("/UserProfiles/",StringComparison.OrdinalIgnoreCase) || string.Equals(FullPath(lbRoot,p),Path.Combine(tpRoot,"UserProfiles",id+".xml"),StringComparison.OrdinalIgnoreCase)) return id;
  }
  if(!string.IsNullOrWhiteSpace(p) && string.Equals(FullPath(lbRoot,p),Path.Combine(tpRoot,"TeknoParrotUi.exe"),StringComparison.OrdinalIgnoreCase)) {
   var m=Regex.Match(Value(e,"CommandLine"),"(?:^|\\s)--profile[= ](?:\"(?<id>[^\"]+)\"|(?<id>[^\\s]+))",RegexOptions.IgnoreCase);
   if(m.Success) { var id=Path.GetFileNameWithoutExtension(m.Groups["id"].Value.Replace('\\','/')); if(known==null || known.Contains(id)) return id; }
  }
  return null;
 }
 private sealed record IdentityCache(DateTime LastWrite,long Length,Dictionary<string,LaunchBoxIdentity> Entries);
 private static readonly Dictionary<string,IdentityCache> IdentityCaches=new(StringComparer.OrdinalIgnoreCase);
 private static readonly object IdentityLock=new();
 public static LaunchBoxIdentity? FindGameIdentity(AppSettings s,GameRecord game) {
  var p=Path.GetFullPath(PlatformFile(s));if(!File.Exists(p))return null;var info=new FileInfo(p);var key=p+"|"+Path.GetFullPath(s.TeknoParrotPath);
  lock(IdentityLock) {
   if(!IdentityCaches.TryGetValue(key,out var cache)||cache.LastWrite!=info.LastWriteTimeUtc||cache.Length!=info.Length) {
    var root=ReadXml(p).Root!;var parents=root.Elements("Game").GroupBy(e=>Value(e,"ID"),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);var entries=new Dictionary<string,LaunchBoxIdentity>(StringComparer.OrdinalIgnoreCase);
    var groups=root.Elements().Where(e=>e.Name.LocalName is "Game" or "AdditionalApplication").Select(e=>(Node:e,Id:ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath))).Where(x=>x.Id!=null).GroupBy(x=>x.Id!,StringComparer.OrdinalIgnoreCase);
    foreach(var group in groups) {var e=group.OrderBy(x=>x.Node.Name.LocalName=="Game"?0:1).First().Node;var parent=e.Name.LocalName=="Game"?e:parents.GetValueOrDefault(Value(e,"GameID"));entries[group.Key]=new(){ProfileId=group.Key,GameGuid=Value(parent,"ID"),DatabaseId=int.TryParse(Value(parent,"DatabaseID"),out var db)&&db>0?db:null,Title=Value(parent,"Title"),ApplicationPath=Value(e,"ApplicationPath"),IsAdditionalApplication=e.Name.LocalName!="Game",Duplicate=group.Skip(1).Any()&&!IsDefaultMirror(group.Select(x=>x.Node).ToList())};}
    if(IdentityCaches.Count>8)IdentityCaches.Clear();cache=new(info.LastWriteTimeUtc,info.Length,entries);IdentityCaches[key]=cache;
   }
   return cache.Entries.GetValueOrDefault(game.Id);
  }
 }
 public static Dictionary<string,string> DiscoverMediaFolders(AppSettings s) {
  var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var type in new[]{"Box - Front","Box - Back","Clear Logo","Fanart - Background","Screenshot - Gameplay","Arcade - Marquee","Banner"}) result[type]=Path.Combine(s.LaunchBoxPath,"Images",s.PlatformName,type);
  result["Video"]=Path.Combine(s.LaunchBoxPath,"Videos",s.PlatformName);
  result["Theme Video"]=Path.Combine(result["Video"],"Theme");
  var p=Path.Combine(s.LaunchBoxPath,"Data","Platforms.xml"); if(!File.Exists(p)) return result;
  var root=ReadXml(p).Root!;var plat=root.Elements("Platform").FirstOrDefault(x=>Value(x,"Name").Equals(s.PlatformName,StringComparison.OrdinalIgnoreCase));
  var old=new Dictionary<string,string>{{"VideosFolder","Video"},{"FrontImagesFolder","Box - Front"},{"BackImagesFolder","Box - Back"},{"ClearLogoImagesFolder","Clear Logo"},{"FanartImagesFolder","Fanart - Background"},{"ScreenshotImagesFolder","Screenshot - Gameplay"},{"BannerImagesFolder","Banner"}};
  foreach(var pair in old) if(!string.IsNullOrWhiteSpace(Value(plat,pair.Key))) result[pair.Value]=FullPath(s.LaunchBoxPath,Value(plat,pair.Key));
  result["Theme Video"]=Path.Combine(result["Video"],"Theme");
  foreach(var folder in root.Elements("PlatformFolder").Where(e=>Value(e,"Platform").Equals(s.PlatformName,StringComparison.OrdinalIgnoreCase))) if(!string.IsNullOrWhiteSpace(Value(folder,"FolderPath"))) result[Value(folder,"MediaType")]=FullPath(s.LaunchBoxPath,Value(folder,"FolderPath"));
  return result;
 }
 private static string? ReadyProfile(string path,string tpRoot) {
  if(!File.Exists(path)) return "TeknoParrot user profile is missing.";
  try { var p=ReadXml(path).Root!; if(Value(p,"DevOnly").Equals("true",StringComparison.OrdinalIgnoreCase)) return "Developer-only profile.";
   var a=Value(p,"GamePath"); if(string.IsNullOrWhiteSpace(a)||!File.Exists(FullPath(tpRoot,a))) return "Primary game file is missing.";
   if(Value(p,"HasTwoExecutables").Equals("true",StringComparison.OrdinalIgnoreCase)) {var b=Value(p,"GamePath2"); if(string.IsNullOrWhiteSpace(b)||!File.Exists(FullPath(tpRoot,b))) return "Secondary game file is missing.";}
   return null;
  } catch(Exception ex) when(ex is IOException or XmlException or ArgumentException) {return "Invalid profile: "+ex.Message;}
 }
 // A subset selected in the UI must still respect a preferred ready profile elsewhere in the catalog.
 // Read only top-level profile/metadata files; do not scan ROM drives or create an AppEngine job store.
 private static Dictionary<string,GameRecord> SelectionCatalog(AppSettings settings,IEnumerable<GameRecord> requested,CancellationToken ct) {
  var result=new Dictionary<string,GameRecord>(StringComparer.OrdinalIgnoreCase);
  var profileFolders=new[]{Path.Combine(settings.TeknoParrotPath,"GameProfiles"),Path.Combine(settings.TeknoParrotPath,"UserProfiles")};
  foreach(var path in profileFolders.Where(Directory.Exists).SelectMany(p=>Directory.EnumerateFiles(p,"*.xml")).Distinct(StringComparer.OrdinalIgnoreCase)) {
   ct.ThrowIfCancellationRequested();var id=Path.GetFileNameWithoutExtension(path);if(result.ContainsKey(id))continue;
   var template=Path.Combine(settings.TeknoParrotPath,"GameProfiles",id+".xml");
   XElement? templateRoot=null;
   try {if(File.Exists(template)){templateRoot=ReadXml(template).Root;if(Value(templateRoot,"DevOnly").Equals("true",StringComparison.OrdinalIgnoreCase))continue;}}
   catch(Exception ex) when(ex is IOException or XmlException or ArgumentException) {continue;}
   var game=new GameRecord{Id=id,Name=id,Emulator=Value(templateRoot,"EmulatorType"),UserProfilePath=Path.Combine(settings.TeknoParrotPath,"UserProfiles",id+".xml")};
   var metadata=Path.Combine(settings.TeknoParrotPath,"Metadata",id+".json");
   if(File.Exists(metadata))try {using var doc=JsonDocument.Parse(File.ReadAllText(metadata));if(doc.RootElement.TryGetProperty("game_name",out var name)&&name.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(name.GetString()))game.Name=name.GetString()!;}
   catch(Exception ex) when(ex is IOException or JsonException) {continue;}
   result[id]=game;
  }
  foreach(var game in requested){if(string.IsNullOrWhiteSpace(game.Emulator)&&result.TryGetValue(game.Id,out var known))game.Emulator=known.Emulator;result[game.Id]=game;}
  return result;
 }
 public Task<LaunchBoxPlan> PreviewAsync(AppSettings settings,IEnumerable<GameRecord> games,IProgress<JobEvent>? progress=null,CancellationToken ct=default) => Task.Run(()=> {
  var s=settings; var source=PlatformFile(s); if(!File.Exists(source)) throw new FileNotFoundException("The existing LaunchBox platform XML was not found. Select its exact platform name.",source);
  var emulatorPath=Path.Combine(s.LaunchBoxPath,"Data","Emulators.xml");var platformPath=Path.Combine(s.LaunchBoxPath,"Data","Platforms.xml");
  var initialHashes=new[]{source,emulatorPath,platformPath}.Where(File.Exists).ToDictionary(Path.GetFullPath,Hash,StringComparer.OrdinalIgnoreCase);
  var plan=new LaunchBoxPlan{PlatformName=s.PlatformName,SourcePath=Path.GetFullPath(source),TeknoParrotPath=Path.GetFullPath(s.TeknoParrotPath),Document=ReadXml(source),MediaFolders=DiscoverMediaFolders(s)};
  foreach(var pair in initialHashes) plan.SourceHashes[pair.Key]=pair.Value;
  var records=games.GroupBy(g=>g.Id,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
  var catalog=SelectionCatalog(s,records,ct);
  string ProfilePath(GameRecord g)=>string.IsNullOrWhiteSpace(g.UserProfilePath)?Path.Combine(plan.TeknoParrotPath,"UserProfiles",g.Id+".xml"):Path.GetFullPath(g.UserProfilePath);
  var requestedFamilies=records.Select(g=>GameSelectionPolicy.FamilyKey(g.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var ready=catalog.Values.Where(g=>requestedFamilies.Contains(GameSelectionPolicy.FamilyKey(g.Name))).ToDictionary(g=>g.Id,g=>ReadyProfile(ProfilePath(g),plan.TeknoParrotPath),StringComparer.OrdinalIgnoreCase);
  var preferred=GameSelectionPolicy.PreferredByFamily(catalog.Values.Where(g=>ready.TryGetValue(g.Id,out var reason)&&reason==null).Select(g=>new GameRecord{Id=g.Id,Name=g.Name,Emulator=g.Emulator,Installed=true,PathsValid=true,UserProfilePath=ProfilePath(g)}));
  var ids=catalog.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
  var root=plan.Document.Root!;var identities=root.Elements().Where(e=>e.Name.LocalName is "Game" or "AdditionalApplication").Select(e=>(e,id:ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath,ids))).Where(t=>t.id!=null).GroupBy(t=>t.id!,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Select(t=>t.e).ToList(),StringComparer.OrdinalIgnoreCase);
  // Resolve curated titles through their exact profile first. Title-only matches block an unsafe add,
  // but cannot authorize repointing an unrelated LaunchBox entry.
  string ExistingFamily(XElement e) {var id=ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath);return GameSelectionPolicy.FamilyKey(id!=null&&catalog.TryGetValue(id,out var known)?known.Name:Value(e,"Title"));}
  var families=root.Elements().Where(e=>e.Name.LocalName=="Game"||(e.Name.LocalName=="AdditionalApplication"&&ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath,ids)!=null))
   .GroupBy(ExistingFamily,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToList(),StringComparer.OrdinalIgnoreCase);
  // Use the effective platform command and filename flags, including LaunchBox's implicit ROM append.
  var launchSetup = new LaunchBoxEmulatorSetupService().Inspect(s,ct);
  var suitable = launchSetup.IsReady && !string.IsNullOrWhiteSpace(launchSetup.EmulatorId);
  using var metadata=LaunchBoxMetadataService.TryOpen(s);
  foreach(var game in records) {
   ct.ThrowIfCancellationRequested();var pp=ProfilePath(game);
   var item=new LaunchBoxPlanItem{ProfileId=game.Id,Name=game.Name,ProfilePath=pp};plan.Items.Add(item);
   identities.TryGetValue(game.Id,out var hits);if(hits!=null&&IsDefaultMirror(hits))hits=hits.Where(e=>e.Name.LocalName=="Game").ToList();
   var family=GameSelectionPolicy.FamilyKey(game.Name);families.TryGetValue(family,out var existingFamily);if(existingFamily!=null&&IsDefaultMirror(existingFamily)&&existingFamily.Select(e=>ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count()==1)existingFamily=existingFamily.Where(e=>e.Name.LocalName=="Game").ToList();
   var loaderPair=LoaderPair(existingFamily,game,catalog,s);

   if(ready[game.Id] is string reason){item.Action="NotReady";item.Detail=reason;item.Selected=false;continue;}
   var winner=preferred[family];
   if(!winner.Id.Equals(game.Id,StringComparison.OrdinalIgnoreCase)){item.Action="SkippedVariant";item.Detail=$"One version per game: prefer {winner.Name} ({winner.Id}), which has valid launch files. Priority: USA, World, then another region; ELF2 is preferred within the same region.";item.Selected=false;continue;}
   var loaderCombination=loaderPair!=null&&GameSelectionPolicy.IsElfLoader2(game)&&existingFamily!.Count(e=>e.Name.LocalName=="Game")<=2&&(hits?.Count??0)<=1;
   if(loaderCombination&&PlanLoaderCombination(s,plan,game,loaderPair!,existingFamily!,item,pp))continue;
   if(existingFamily?.Count>1&&!loaderCombination&&PlanFamilyConsolidation(s,plan,game,existingFamily,catalog,item,pp))continue;
   var replacingVariant=false;
   if(hits==null&&existingFamily?.Count==1) {
    var existing=existingFamily[0];var oldId=ProfileId(existing,s.LaunchBoxPath,s.TeknoParrotPath,ids);
    if(existing.Name.LocalName!="Game"||oldId==null||!Value(existing,"ApplicationPath").EndsWith(".xml",StringComparison.OrdinalIgnoreCase)) {item.Action="NeedsReview";item.Detail="This game family already exists, but its custom launch command or additional-application identity needs review before changing versions. No duplicate will be added.";item.Selected=false;continue;}
    hits=new(){existing};replacingVariant=true;
    if(catalog.TryGetValue(oldId,out var oldGame)&&Value(existing,"Title").Equals(oldGame.Name,StringComparison.OrdinalIgnoreCase))item.Changes["Title"]=game.Name;
   }
   item.ProfileHash=Hash(pp); var path=Path.GetRelativePath(s.LaunchBoxPath,pp);
   if(hits?.Count==1) {
    var e=hits[0];item.ElementName=e.Name.LocalName;item.GameGuid=Value(e,item.ElementName=="Game"?"ID":"Id");
    if(!string.Equals(FullPath(s.LaunchBoxPath,Value(e,"ApplicationPath").Trim('"')),pp,StringComparison.OrdinalIgnoreCase)) {
     if(Value(e,"ApplicationPath").EndsWith(".xml",StringComparison.OrdinalIgnoreCase)) item.Changes["ApplicationPath"]=path;
     else {item.Action="Unchanged";item.Detail="Existing direct launch command preserved.";item.Selected=false;continue;}
    }
    if(item.ElementName=="Game") {item.DatabaseId=int.TryParse(Value(e,"DatabaseID"),out var db)&&db>0?db:null;if(s.FillMissingMetadata) FillMetadata(e,game,item,metadata);}
    item.Action=item.Changes.Count>0?"Update":"Unchanged";item.Detail=item.Changes.Count>0?(replacingVariant?"Use the preferred ready version in the existing game; retain its ID, play history, favorites and hooks. ":"")+string.Join("; ",item.Changes.Select(c=>$"Fill/update {c.Key}: {c.Value}")):"Exact profile already represented; metadata, history and hooks preserved.";
   } else {
    if(!suitable){item.Action="NeedsReview";item.Detail="TeknoParrot launch setup: "+launchSetup.Detail+" Open Setup > Check launch setup to inspect or repair the configuration.";item.Selected=false;continue;}
    item.GameGuid=Guid.NewGuid().ToString();item.NewGame=new XElement("Game",new XElement("ID",item.GameGuid),new XElement("Title",game.Name),new XElement("Platform",s.PlatformName),new XElement("ApplicationPath",path),new XElement("Emulator",launchSetup.EmulatorId),new XElement("CommandLine",""),new XElement("DateAdded",DateTimeOffset.Now.ToString("O")),new XElement("DateModified",DateTimeOffset.Now.ToString("O")),new XElement("Favorite","false"),new XElement("PlayCount",0),new XElement("PlayTime",0),new XElement("Hide","false"),new XElement("Broken","false"),new XElement("UseDosBox","false"),new XElement("UseScummVM","false"),new XElement("Source","Arcade Library Manager"));
    if(s.FillMissingMetadata) FillMetadata(item.NewGame,game,item,metadata);
    item.Action="Add";item.Detail="Add the preferred ready version (USA, then World, then another region) using the existing platform and emulator; controls remain in TeknoParrot.";
   }
  }
  LaunchBoxRelatedFiles.Prepare(plan);
  foreach(var pair in plan.SourceHashes) if(!File.Exists(pair.Key)||Hash(pair.Key)!=pair.Value) throw new InvalidOperationException("LaunchBox data changed during preview. Scan again.");
  progress?.Report(new("LaunchBox",$"Preview: {plan.Items.Count(i=>i.Action=="Add")} additions, {plan.Items.Count(i=>i.Action=="Update")} updates, {plan.Items.Count(i=>i.Action=="NeedsReview")} need review."));return plan;
 },ct);
 private static void FillMetadata(XElement e,GameRecord game,LaunchBoxPlanItem item,LaunchBoxMetadataCatalog? metadata) {
  var match=metadata?.FindExact(game.Name,item.DatabaseId);
  var fields=new Dictionary<string,string>{{"Title",game.Name},{"Genre",game.Genre},{"Notes",game.Notes}};
  if(int.TryParse(game.Year,out var y)&&y>=1000&&y<=9999) fields["ReleaseDate"]=$"{y:0000}-01-01";
  if(match!=null){if(item.DatabaseId==null){fields["DatabaseID"]=match.DatabaseId.ToString();item.DatabaseId=match.DatabaseId;}foreach(var pair in match.Fields)if(!string.IsNullOrWhiteSpace(pair.Value))fields[pair.Key]=pair.Value;}
  foreach(var f in fields)if(string.IsNullOrWhiteSpace(Value(e,f.Key))&&!string.IsNullOrWhiteSpace(f.Value))item.Changes[f.Key]=f.Value;
 }
 // A PreserveWhitespace document contains old indentation text. Appending unformatted records to it
 // makes XmlWriter suppress indentation for those records. Normalize only structural whitespace;
 // keep leaf values, mixed content, comments, processing instructions and xml:space content intact.
 internal static void NormalizeFormatting(XDocument document) {
  foreach(var element in document.Descendants()) {
   var space=element.AncestorsAndSelf().Select(e=>e.Attribute(XNamespace.Xml+"space")?.Value).FirstOrDefault(v=>v!=null);
   if(space!="preserve"&&element.HasElements&&!element.Nodes().OfType<XText>().Any(t=>t is XCData||!string.IsNullOrWhiteSpace(t.Value)))
    foreach(var text in element.Nodes().OfType<XText>().Where(t=>t.GetType()==typeof(XText)&&string.IsNullOrWhiteSpace(t.Value)).ToList())text.Remove();
   // LaunchBox's own serializer writes empty scalar fields as <Field />.
   if(!element.HasElements&&element.Nodes().All(n=>n.GetType()==typeof(XText)&&((XText)n).Value.Length==0))element.RemoveNodes();
  }
 }
 public async Task<OperationResult> ApplyAsync(LaunchBoxPlan plan,IProgress<JobEvent>? progress=null,CancellationToken ct=default) {
  await CommitGate.WaitAsync(ct);try {
   if(isRunning())throw new InvalidOperationException("Close LaunchBox and Big Box before committing this preview.");
   await volumeHealth.EnsureWritableAsync(new[]{plan.SourcePath,Path.Combine(Path.GetDirectoryName(plan.SourcePath)!,"..","..","Backups","ArcadeLibraryManager")},ct);
   foreach(var pair in plan.SourceHashes)if(!File.Exists(pair.Key)||Hash(pair.Key)!=pair.Value)throw new InvalidOperationException("LaunchBox data changed after preview. Scan again before committing.");
   var selected=plan.Items.Where(i=>i.Selected&&i.Action is "Add" or "Update" or "Combine" or "Consolidate").ToList();if(selected.Count==0)return new(0,plan.Items.Count,new());
   var doc=new XDocument(plan.Document);
   foreach(var item in selected) {ct.ThrowIfCancellationRequested();if(!File.Exists(item.ProfilePath)||Hash(item.ProfilePath)!=item.ProfileHash||ReadyProfile(item.ProfilePath,plan.TeknoParrotPath)!=null)throw new InvalidOperationException($"{item.Name}: profile or launch files changed; scan again.");
    XElement e;if(item.Action=="Add"){e=new XElement(item.NewGame!);doc.Root!.Add(e);}else e=doc.Root!.Elements(item.ElementName).Single(x=>Value(x,item.ElementName=="Game"?"ID":"Id")==item.GameGuid);
    foreach(var pair in item.Changes)e.SetElementValue(pair.Key,pair.Value);
    ApplyCombination(doc,item);
   }
   return await LaunchBoxTransaction.CommitAsync(plan,doc,isRunning,volumeHealth,progress,ct);
  }finally{CommitGate.Release();}
 }
 public async Task RollbackAsync(string journalPath,CancellationToken ct=default) {
  await CommitGate.WaitAsync(ct);try {await LaunchBoxTransaction.RollbackAsync(journalPath,isRunning,volumeHealth,ct);}finally {CommitGate.Release();}
 }
}



