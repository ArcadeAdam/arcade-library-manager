
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed class AppEngine {
 readonly AppSettings settings;readonly string data;readonly IProgress<JobEvent>? progress;readonly JobStore jobs;
 readonly Dictionary<string,GameRecipe> recipes;readonly ArchiveSource source;readonly VolumeHealthService volumeHealth;
 public AppEngine(AppSettings settings,string dataDir,IProgress<JobEvent>? progress=null,VolumeHealthService? volumeHealth=null){
  this.settings=settings;this.volumeHealth=volumeHealth??new();data=Path.GetFullPath(dataDir);Directory.CreateDirectory(data);this.progress=progress;jobs=new(data);source=new(settings,data,progress);
  var user=Path.Combine(data,"recipes.json");var bundled=Path.Combine(AppContext.BaseDirectory,"assets","recipes.json");
  var path=File.Exists(user)?user:bundled;
  recipes=File.Exists(path)?(JsonSerializer.Deserialize<List<GameRecipe>>(File.ReadAllText(path),SettingsStore.Json)??new()).ToDictionary(r=>r.Id,StringComparer.OrdinalIgnoreCase):new(StringComparer.OrdinalIgnoreCase);
 }
 public List<JobRecord> GetJobs()=>jobs.List();
 public List<InstallPlanItem> GetPendingPlan()=>jobs.Pending();
 public async Task<List<InstallPlanItem>> ReviewPendingPlanAsync(CancellationToken ct){
  var plan=jobs.Pending();ApplySelectionPolicy(plan,await ScanAsync(ct));return plan;
 }
 public Task<List<GameRecord>> ScanAsync(CancellationToken ct)=>Task.Run(()=>{
  var directory=Path.Combine(settings.TeknoParrotPath,"GameProfiles");
  if(!Directory.Exists(directory))throw new DirectoryNotFoundException("Choose a TeknoParrot folder containing GameProfiles.");
  var result=new List<GameRecord>();
  foreach(var path in Directory.EnumerateFiles(directory,"*.xml")){
   ct.ThrowIfCancellationRequested();
   try{
    var template=XDocument.Load(path).Root!;if(Bool(template,"DevOnly"))continue;
    var id=Path.GetFileNameWithoutExtension(path);var user=Path.Combine(settings.TeknoParrotPath,"UserProfiles",id+".xml");
    var g=new GameRecord{Id=id,Name=id,TemplatePath=path,UserProfilePath=user,Emulator=Value(template,"EmulatorType"),ExecutableName=Value(template,"ExecutableName"),ExecutableName2=Value(template,"ExecutableName2"),HasTwoExecutables=Bool(template,"HasTwoExecutables"),SubscriptionRequired=Bool(template,"Patreon")||Bool(template,"IsTpoExclusive"),Installed=File.Exists(user)};
    var metadata=Path.Combine(settings.TeknoParrotPath,"Metadata",id+".json");
    if(File.Exists(metadata)){using var md=JsonDocument.Parse(File.ReadAllText(metadata));g.Name=JsonString(md,"game_name",id);g.Notes=JsonString(md,"general_issues","");g.Genre=JsonString(md,"game_genre","");g.Year=JsonString(md,"release_year","");}
    if(string.IsNullOrWhiteSpace(g.Name))g.Name=id;
    if(g.Installed){var installed=XDocument.Load(user).Root!;g.GamePath=ResolveGamePath(Value(installed,"GamePath"));g.GamePath2=ResolveGamePath(Value(installed,"GamePath2"));g.PathsValid=File.Exists(g.GamePath)&&(!g.HasTwoExecutables||File.Exists(g.GamePath2));}
    g.Status=g.Installed?(g.PathsValid?"Installed — paths present":"Needs path repair"):recipes.ContainsKey(id)?"Not installed":"Recipe needed";
    result.Add(g);
   }catch(Exception e)when(e is System.Xml.XmlException or JsonException or IOException){progress?.Report(new("Scan warning",Path.GetFileName(path)+": "+e.Message));}
  }
  progress?.Report(new("Scanned",$"{result.Count:N0} released profiles; {result.Count(g=>g.PathsValid):N0} installed paths present."));
  return result.OrderBy(g=>g.Name,StringComparer.OrdinalIgnoreCase).ToList();
 },ct);
 string ResolveGamePath(string path)=>string.IsNullOrWhiteSpace(path)?"":Path.IsPathRooted(path)?path:Path.GetFullPath(Path.Combine(settings.TeknoParrotPath,path));
 static string JsonString(JsonDocument j,string key,string fallback)=>j.RootElement.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString():fallback;
 static string Value(XElement e,string name)=>e.Element(name)?.Value??"";
 static bool Bool(XElement e,string name)=>bool.TryParse(Value(e,name),out var b)&&b;
 public Task IndexRomsAsync(CancellationToken ct)=>Task.Run(()=>{
  var names=recipes.Values.SelectMany(r=>new[]{Path.GetFileName(r.PrimaryPath.Replace('\\','/')),Path.GetFileName(r.SecondaryPath.Replace('\\','/'))}).Where(n=>!string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var roots=settings.RomRoots.Append(settings.DestinationPath).Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p=>p.Length).ToList();
  roots=roots.Where(r=>!roots.Any(p=>p.Length<r.Length&&r.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))).ToList();
  foreach(var root in roots){
   progress?.Report(new("Indexing","Scanning "+root));
   jobs.Index(root,SafeFiles.Files(root,ct,msg=>progress?.Report(new("Scan warning",msg))).Where(f=>names.Contains(Path.GetFileName(f))||new[]{".zip",".7z",".rar",".chd",".acgame"}.Contains(Path.GetExtension(f),StringComparer.OrdinalIgnoreCase)),ct,progress);
  }
  progress?.Report(new("Indexed","Local index updated. Only relevant files are retained in the database."));
 },ct);
 List<string> Find(string name){
  var paths=jobs.Find(name);
  foreach(var root in settings.RomRoots.Append(settings.DestinationPath)){
   if(string.IsNullOrWhiteSpace(root)||!Directory.Exists(root))continue;
   foreach(var folder in new[]{root,Path.Combine(root,"roms"),Path.Combine(root,"MAME","roms")}){
    var candidate=Path.Combine(folder,name);if(File.Exists(candidate))paths.Add(candidate);
   }
  }
  return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
 }
 public async Task<List<InstallPlanItem>> PlanAsync(IEnumerable<string> ids,CancellationToken ct){
  var selected=ids.ToHashSet(StringComparer.OrdinalIgnoreCase);var games=await ScanAsync(ct);var plan=new List<InstallPlanItem>();
  foreach(var g in games.Where(g=>selected.Contains(g.Id))){
   ct.ThrowIfCancellationRequested();progress?.Report(new("Planning",g.Name,g.Id));
   var item=new InstallPlanItem{ProfileId=g.Id,Name=g.Name,TemplateHash=SafeFiles.Hash(g.TemplatePath),OriginalProfileHash=g.Installed?SafeFiles.Hash(g.UserProfilePath):""};
   if(g.PathsValid){item.Action="Already installed";item.Detail="Existing launch paths retained.";item.Selected=false;plan.Add(item);continue;}
   if(!recipes.TryGetValue(g.Id,out var r)){item.Action="Needs review";item.Detail="No verified recipe. Add a recipe before installing.";item.Selected=false;plan.Add(item);continue;}
   item.Recipe=r;
   try{
    if(string.IsNullOrWhiteSpace(settings.DestinationPath))throw new InvalidDataException("Choose the destination for new games.");
    item.Destination=SafeFiles.Child(settings.DestinationPath,r.PayloadId);
    if(r.Roms.Count>0){
     var validation=await Task.Run(()=>GameValidation.RomSet(r,Find,false,ct),ct);
     if(validation.Success){
      if(ReviewUnownedDestination(item)){plan.Add(item);continue;}
      item.Action="Use local ROMs";item.Detail=validation.Message+"; selected ZIPs are rechecked before copying.";
      item.PrimaryPath=Find(r.RomSet+".zip").FirstOrDefault(p=>validation.ZipPaths.Contains(p))??validation.ZipPaths.FirstOrDefault()??"";
      item.SecondaryPath=validation.DiskPath;item.LocalDependencies=validation.ZipPaths;
      if(string.IsNullOrWhiteSpace(item.PrimaryPath))throw new InvalidDataException("No primary ROM ZIP found.");
      if(g.HasTwoExecutables&&!File.Exists(item.SecondaryPath))throw new InvalidDataException("Secondary launch media is missing.");
      plan.Add(item);continue;
     }
     item.Detail=validation.Message;
    }
    if(string.IsNullOrEmpty(r.ArchiveName)){
     item.Action="Needs review";item.Detail="No verified download mapping is configured for this game; nothing has been downloaded by this plan."+(string.IsNullOrEmpty(item.Detail)?"":" "+item.Detail);item.Selected=false;plan.Add(item);continue;
    }
    var localArchives=Find(Path.GetFileName(r.ArchiveName));
    item.ArchivePath=localArchives.FirstOrDefault(p=>new FileInfo(p).Length==r.ArchiveSize)??"";
    var archive=item.ArchivePath==""?await source.FindAsync(r,ct):null;
    if(item.ArchivePath==""&&archive==null)throw new InvalidDataException("Exact archive/version is not available in the configured source.");
    item.DownloadUrl=archive?.Url??"";
    item.DownloadBytes=item.ArchivePath==""?r.ArchiveSize:0;
    if(r.Roms.Count==0&&r.PrimaryPath!=""){
     var candidates=Find(Path.GetFileName(r.PrimaryPath.Replace('\\','/')));
     if(candidates.Count>0){
      var candidate=await Task.Run(()=>TryExistingPayload(r,candidates,item.ArchivePath,archive,ct),ct);
      if(candidate!=null){item.Action="Use local files";item.PrimaryPath=candidate.Value.Primary;item.SecondaryPath=candidate.Value.Secondary;item.Detail="Archive member sizes and launch-file CRC match the exact edition.";item.DownloadBytes=0;plan.Add(item);continue;}
     }
    }
    if(ReviewUnownedDestination(item)){plan.Add(item);continue;}
    item.Action=item.ArchivePath!=""?"Extract local archive":"Download and install";
    item.Detail=(item.Detail.Length>0?item.Detail+". ":"")+"Verify archive checksum, extract and validate before registration.";
   }catch(Exception e)when(e is not OperationCanceledException){item.Action="Needs review";item.Detail=e.Message;item.Selected=false;}
   plan.Add(item);
  }
  ApplySelectionPolicy(plan,games);return plan;
 }
 static bool ReviewUnownedDestination(InstallPlanItem item){
  if(!Directory.Exists(item.Destination)||File.Exists(Path.Combine(item.Destination,".alm-install.json")))return false;
  item.Action="Needs review";item.Selected=false;
  item.Detail="Destination already exists without this app's completion marker: "+item.Destination+". Existing files are preserved. Index the existing ROM sources to check for an exact reusable edition, or choose a different new-game destination before rebuilding the plan.";
  return true;
 }
 static void ApplySelectionPolicy(List<InstallPlanItem> plan,IReadOnlyList<GameRecord> games){
  var preferred=PreferredInstallCandidates(plan,games);
  foreach(var item in plan){
   var game=games.SingleOrDefault(g=>g.Id.Equals(item.ProfileId,StringComparison.OrdinalIgnoreCase));
   if(game==null){item.Action="Needs review";item.Selected=false;item.Detail="Profile is no longer in the released catalog. Original checkpoint retained.";continue;}
   var winner=preferred.GetValueOrDefault(GameSelectionPolicy.FamilyKey(game.Name));
   if(winner!=null&&!winner.Id.Equals(game.Id,StringComparison.OrdinalIgnoreCase)){
    item.Action="Alternate version";item.Selected=false;
    item.Detail=$"One version per game: {winner.Name} ({winner.Id}) is selected. Preference: USA, then World, then other regions. Existing ready profiles are retained.";
   }else if(game.PathsValid){item.Action="Already installed";item.Selected=false;item.Detail="Existing launch paths retained.";}
  }
 }
 static bool Installable(InstallPlanItem item)=>item.Action is "Use local ROMs" or "Use local files" or "Extract local archive" or "Download and install";
 static Dictionary<string,GameRecord> PreferredInstallCandidates(IReadOnlyList<InstallPlanItem> plan,IReadOnlyList<GameRecord> games){
  var planned=plan.ToDictionary(p=>p.ProfileId,StringComparer.OrdinalIgnoreCase);
  var result=new Dictionary<string,GameRecord>(StringComparer.OrdinalIgnoreCase);
  foreach(var group in games.Where(g=>planned.ContainsKey(g.Id)).GroupBy(g=>GameSelectionPolicy.FamilyKey(g.Name))){
   // Retain a registered, ready sibling instead of creating a second TeknoParrot user profile.
   var ready=games.Where(g=>g.PathsValid&&GameSelectionPolicy.FamilyKey(g.Name)==group.Key).ToList();
   var available=group.Where(g=>Installable(planned[g.Id])).ToList();
   result[group.Key]=GameSelectionPolicy.SelectPreferred(ready.Count>0?ready:available.Count>0?available:group).Single();
  }
  return result;
 }
 bool ReadySibling(GameRecord game,IReadOnlyList<GameRecord> catalog){
  foreach(var sibling in catalog.Where(g=>!g.Id.Equals(game.Id,StringComparison.OrdinalIgnoreCase)&&GameSelectionPolicy.FamilyKey(g.Name)==GameSelectionPolicy.FamilyKey(game.Name))){
   if(!File.Exists(sibling.UserProfilePath))continue;
   try{var profile=XDocument.Load(sibling.UserProfilePath).Root!;if(File.Exists(ResolveGamePath(Value(profile,"GamePath")))&&(!sibling.HasTwoExecutables||File.Exists(ResolveGamePath(Value(profile,"GamePath2")))))return true;}
   catch(Exception e)when(e is IOException or System.Xml.XmlException){/* A broken sibling is not ready. */}
  }
  return false;
 }
 (string Primary,string Secondary)? TryExistingPayload(GameRecipe r,List<string> candidates,string localArchive,ArchiveSource.ArchiveFile? remote,CancellationToken ct){
  if(string.IsNullOrEmpty(r.PrimaryPath))return null;
  using Stream stream=localArchive!=""?File.OpenRead(localArchive):new HttpRangeStream(remote!.Url,remote.Size,ct);
  using var zip=new ZipArchive(stream,ZipArchiveMode.Read);var primaryEntry=FindEntry(zip,r.PrimaryPath);
  if(primaryEntry==null)return null;
  foreach(var candidate in candidates){
   ct.ThrowIfCancellationRequested();
   if(new FileInfo(candidate).Length!=primaryEntry.Length||Crc32.FileHash(candidate)!=primaryEntry.Crc32.ToString("x8"))continue;
   var relative=primaryEntry.FullName.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar);
   if(!candidate.EndsWith(relative,StringComparison.OrdinalIgnoreCase))continue;
   var root=candidate[..^relative.Length].TrimEnd(Path.DirectorySeparatorChar);
   bool complete=true;
   foreach(var e in zip.Entries.Where(e=>e.Name!="")){
    var path=SafeFiles.Child(root,e.FullName);
    if(!File.Exists(path)||new FileInfo(path).Length!=e.Length){complete=false;break;}
   }
   if(!complete)continue;
   var second=string.IsNullOrEmpty(r.SecondaryPath)?"":FindEntry(zip,r.SecondaryPath)?.FullName;
   if(!string.IsNullOrEmpty(r.SecondaryPath)&&second==null)continue;
   if(!string.IsNullOrEmpty(second)){var secondEntry=FindEntry(zip,r.SecondaryPath)!;var secondFile=SafeFiles.Child(root,second);if(Crc32.FileHash(secondFile)!=secondEntry.Crc32.ToString("x8"))continue;}
   GameValidation.Acgame(candidate);
   return(candidate,string.IsNullOrEmpty(second)?"":SafeFiles.Child(root,second));
  }return null;
 }
 static ZipArchiveEntry? FindEntry(ZipArchive zip,string expected){
  var normalized=expected.Replace('\\','/').TrimStart('/');
  var direct=zip.Entries.FirstOrDefault(e=>e.FullName.Equals(normalized,StringComparison.OrdinalIgnoreCase));if(direct!=null)return direct;
  var matches=zip.Entries.Where(e=>e.FullName.EndsWith("/"+normalized,StringComparison.OrdinalIgnoreCase)).ToArray();
  return matches.Length==1?matches[0]:null;
 }
 public async Task<OperationResult> ExecuteAsync(IEnumerable<InstallPlanItem> input,CancellationToken ct){
  var plan=input.Where(p=>p.Selected).GroupBy(p=>p.ProfileId,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();var errors=new List<string>();int success=0,skipped=0;
  ct.ThrowIfCancellationRequested();
  if(plan.Count==0)return new(0,0,errors);
  var cacheRoot=string.IsNullOrWhiteSpace(settings.CachePath)?Path.Combine(settings.DestinationPath,".alm-downloads"):settings.CachePath;
  await volumeHealth.EnsureWritableAsync(plan.Select(p=>p.Destination).Where(p=>!string.IsNullOrWhiteSpace(p)).Concat(new[]{settings.DestinationPath,settings.TeknoParrotPath,data,cacheRoot}),ct);
  Directory.CreateDirectory(settings.DestinationPath);
  var lockPath=Path.Combine(settings.TeknoParrotPath,".arcade-library-manager.lock");
  await using var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
  var currentGames=await ScanAsync(ct);var preferred=PreferredInstallCandidates(plan,currentGames);
  foreach(var p in plan)jobs.Set(p.ProfileId,p.Name,"Queued",p.Detail,p);
  foreach(var p in plan){
   ct.ThrowIfCancellationRequested();
   var current=currentGames.SingleOrDefault(g=>g.Id.Equals(p.ProfileId,StringComparison.OrdinalIgnoreCase));
   if(!Installable(p)||p.Recipe==null){skipped++;jobs.Set(p.ProfileId,p.Name,"Skipped",p.Detail);continue;}
   if(current==null||!preferred.TryGetValue(GameSelectionPolicy.FamilyKey(current.Name),out var winner)||!winner.Id.Equals(current.Id,StringComparison.OrdinalIgnoreCase)){
    skipped++;jobs.Set(p.ProfileId,p.Name,"Skipped","Another version of this game is preferred or already installed. Rebuild the preview for the current selection.");continue;
   }
   try{
    var r=p.Recipe;var template=SafeFiles.Child(Path.Combine(settings.TeknoParrotPath,"GameProfiles"),p.ProfileId+".xml");
    if(SafeFiles.Hash(template)!=p.TemplateHash)throw new IOException("TeknoParrot template changed since preview; rebuild the plan.");
    var profile=SafeFiles.Child(Path.Combine(settings.TeknoParrotPath,"UserProfiles"),p.ProfileId+".xml");
    if(File.Exists(profile)&&p.OriginalProfileHash==""){skipped++;jobs.Set(p.ProfileId,p.Name,"Skipped","Profile was added by another process; retained unchanged.");continue;}
    if(p.OriginalProfileHash!=""&&(!File.Exists(profile)||SafeFiles.Hash(profile)!=p.OriginalProfileHash))throw new IOException("Existing profile changed since preview; rebuild the plan.");
    string primary=p.PrimaryPath,secondary=p.SecondaryPath;
    void Stage(string stage,string message){jobs.Set(p.ProfileId,p.Name,stage,message);progress?.Report(new(stage,message,p.ProfileId));}
    if(p.Action=="Use local ROMs"){
     Stage("Verifying","Checking all required ROM contents and CHD identities");
     var validation=await Task.Run(()=>GameValidation.RomSet(r,Find,true,ct),ct);
     if(!validation.Success)throw new InvalidDataException(validation.Message);
     var stage=p.Destination+".alm-staging";PrepareStage(stage,r);
     foreach(var sourcePath in validation.ZipPaths){
      ct.ThrowIfCancellationRequested();var target=SafeFiles.Child(stage,Path.GetFileName(sourcePath));
      await CopyVerifiedAsync(sourcePath,target,ct);
     }
     var selected=p.PrimaryPath;
     if(!validation.ZipPaths.Contains(selected,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("The selected ROM source no longer validates.");
     var alias=SafeFiles.Child(stage,r.RomSet+".zip");if(!Path.GetFileName(selected).Equals(r.RomSet+".zip",StringComparison.OrdinalIgnoreCase))await CopyVerifiedAsync(selected,alias,ct);
     var stageMoved=CommitStage(stage,p.Destination,r);
     primary=SafeFiles.Child(p.Destination,r.RomSet+".zip");
     // A failed extraction may already contain a valid CHD in our staging folder.
     // Committing moves that folder, so the registered secondary path must move too.
     secondary=stageMoved&&validation.DiskPath.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?p.Destination+validation.DiskPath[stage.Length..]:validation.DiskPath;
    }else if(p.Action!="Use local files"){
     var marker=Path.Combine(p.Destination,".alm-install.json");
     if(!File.Exists(marker)){
      if(Directory.Exists(p.Destination))throw new IOException("Destination already exists without this app's completion marker; existing files preserved.");
      var cache=string.IsNullOrWhiteSpace(settings.CachePath)?Path.Combine(settings.DestinationPath,".alm-downloads"):settings.CachePath;
      Directory.CreateDirectory(cache);
      var archive=p.ArchivePath;
      if(archive==""){
       archive=SafeFiles.Child(cache,r.PayloadId+"-"+Path.GetFileName(r.ArchiveName));
       if(!File.Exists(archive)){Stage("Downloading",p.Name);await new DownloadService(settings.MaxDownloadMbps,progress).GetAsync(p.DownloadUrl,archive,r.ArchiveSize,ct);}
      }
      Stage("Verifying","Verifying complete archive checksum");
      await DownloadService.VerifyAsync(archive,r.ArchiveSize,r.ArchiveMd5,r.ArchiveSha1,ct);
      var stage=p.Destination+".alm-staging";PrepareStage(stage,r);Stage("Extracting","Extracting into isolated staging");
      await Task.Run(()=>SafeFiles.ExtractZip(archive,stage,ct,progress),ct);
      var extractedPrimary=Locate(stage,r.PrimaryPath);
      if(r.Roms.Count>0){
       var inside=SafeFiles.Files(stage,ct).ToLookup(Path.GetFileName,StringComparer.OrdinalIgnoreCase);
       var validation=await Task.Run(()=>GameValidation.RomSet(r,n=>inside[n].Concat(Find(n)).ToList(),true,ct),ct);
       if(!validation.Success)throw new InvalidDataException(validation.Message);
       foreach(var dependency in validation.ZipPaths.Where(f=>!Path.GetFullPath(f).StartsWith(Path.GetFullPath(stage)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))await CopyVerifiedAsync(dependency,SafeFiles.Child(Path.GetDirectoryName(extractedPrimary)!,Path.GetFileName(dependency)),ct);
       secondary=validation.DiskPath.StartsWith(stage+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?p.Destination+validation.DiskPath[stage.Length..]:validation.DiskPath;
      }
      GameValidation.Acgame(extractedPrimary);
      CommitStage(stage,p.Destination,r);
     }else{
      var completed=JsonSerializer.Deserialize<GameRecipe>(File.ReadAllText(marker),SettingsStore.Json);
      if(completed?.ArchiveSha1!=r.ArchiveSha1||completed?.ArchiveMd5!=r.ArchiveMd5)throw new InvalidDataException("Installed payload marker does not match the planned edition.");
     }
     primary=Locate(p.Destination,r.PrimaryPath);
     if(!string.IsNullOrEmpty(r.SecondaryPath))secondary=Locate(p.Destination,r.SecondaryPath);
     else if(secondary==""&&r.Disks.Count>0){var existing=GameValidation.RomSet(r,n=>SafeFiles.Files(p.Destination,ct).Where(f=>Path.GetFileName(f).Equals(n,StringComparison.OrdinalIgnoreCase)).Concat(Find(n)).ToList(),false,ct);secondary=existing.DiskPath;}
    }
    Stage("Validating","Checking launch paths and emulator dependencies");
    if(!File.Exists(primary))throw new FileNotFoundException("Primary launch file is missing.",primary);
    var baseDoc=XDocument.Load(p.OriginalProfileHash==""?template:profile);
    if(Bool(baseDoc.Root!,"HasTwoExecutables")&&!File.Exists(secondary))throw new FileNotFoundException("Secondary launch file is missing.",secondary);
    GameValidation.Acgame(primary);
    if(p.Action=="Use local files"){
     var archive=p.ArchivePath;var remote=archive==""?await source.FindAsync(r,ct):null;
     var verified=await Task.Run(()=>TryExistingPayload(r,new(){primary},archive,remote,ct),ct);
     if(verified==null)throw new IOException("Local payload changed or no longer matches its archive.");
    }
    if(r.Roms.Count>0){
     var installedFiles=SafeFiles.Files(Path.GetDirectoryName(primary)!,ct).ToLookup(Path.GetFileName,StringComparer.OrdinalIgnoreCase);
     var installedValidation=await Task.Run(()=>GameValidation.RomSet(r,n=>installedFiles[n].Any()?installedFiles[n].ToList():Find(n),true,ct),ct);
     if(!installedValidation.Success)throw new InvalidDataException("Installed ROM contents failed validation: "+installedValidation.Message);
    }
    if(!File.Exists(profile)&&ReadySibling(current,currentGames)){
     skipped++;jobs.Set(p.ProfileId,p.Name,"Skipped","Another ready version was registered during installation; no second profile was added.");continue;
    }
    await volumeHealth.EnsureWritableAsync(new[]{profile,data},ct);
    Stage("Registering","Saving launch paths; retaining existing control settings");
    Set(baseDoc.Root!,"GamePath",primary);if(secondary!="")Set(baseDoc.Root!,"GamePath2",secondary);
    Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
    if(p.OriginalProfileHash!=""){
     if(SafeFiles.Hash(profile)!=p.OriginalProfileHash)throw new IOException("Profile changed during installation; original retained.");
     var backup=Path.Combine(data,"backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"),p.ProfileId+".xml");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.Copy(profile,backup,false);
    }
    var temporary=profile+"."+Guid.NewGuid().ToString("N")+".tmp";baseDoc.Save(temporary);
    if(p.OriginalProfileHash!=""&&SafeFiles.Hash(profile)!=p.OriginalProfileHash){File.Delete(temporary);throw new IOException("Profile changed before commit; existing controls retained.");}
    if(p.OriginalProfileHash=="")File.Move(temporary,profile,false);else File.Move(temporary,profile,true);
    jobs.Set(p.ProfileId,p.Name,"Complete","Installed; launch paths verified. Controls remain user-managed.");success++;
   }catch(OperationCanceledException){jobs.Set(p.ProfileId,p.Name,"Paused","Cancelled safely; partial downloads/staging retained.");throw;}
   catch(Exception e){jobs.Set(p.ProfileId,p.Name,"Failed",e.Message);errors.Add(p.Name+": "+e.Message);progress?.Report(new("Failed",e.Message,p.ProfileId));}
  }
  return new(success,skipped,errors);
 }
 static void Set(XElement root,string name,string value){var el=root.Element(name);if(el==null)root.Add(new XElement(name,value));else el.Value=value;}
 static async Task CopyVerifiedAsync(string from,string to,CancellationToken ct){
  Directory.CreateDirectory(Path.GetDirectoryName(to)!);if(Path.GetFullPath(from).Equals(Path.GetFullPath(to),StringComparison.OrdinalIgnoreCase))return;
  await using(var input=File.OpenRead(from))await using(var output=new FileStream(to,FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,true))await input.CopyToAsync(output,ct);
  if(SafeFiles.Hash(from)!=SafeFiles.Hash(to))throw new InvalidDataException("Copied file checksum mismatch.");
 }
 static void PrepareStage(string stage,GameRecipe recipe){
  SafeFiles.Child(Path.GetDirectoryName(stage)!,Path.GetFileName(stage));
  var marker=Path.Combine(stage,".alm-staging-owner.json");
  if(Directory.Exists(stage)){
   if(!File.Exists(marker))throw new IOException("Staging folder exists without this app's ownership marker; preserved for review.");
   var old=JsonSerializer.Deserialize<GameRecipe>(File.ReadAllText(marker),SettingsStore.Json);
   if(old?.PayloadId!=recipe.PayloadId||old.ArchiveSha1!=recipe.ArchiveSha1||old.ArchiveMd5!=recipe.ArchiveMd5)throw new IOException("Staging folder belongs to a different payload.");
  }else{Directory.CreateDirectory(stage);SafeFiles.WriteText(marker,JsonSerializer.Serialize(recipe,SettingsStore.Json));}
 }
 bool CommitStage(string stage,string destination,GameRecipe recipe){
  volumeHealth.EnsureWritable(new[]{stage,destination});
  if(Directory.Exists(destination)){
   if(File.Exists(Path.Combine(destination,".alm-install.json"))){var old=JsonSerializer.Deserialize<GameRecipe>(File.ReadAllText(Path.Combine(destination,".alm-install.json")),SettingsStore.Json);if(old?.PayloadId==recipe.PayloadId&&old.ArchiveSha1==recipe.ArchiveSha1&&old.ArchiveMd5==recipe.ArchiveMd5&&old.RomSet==recipe.RomSet)return false;throw new IOException("Existing payload marker differs from planned recipe.");}
   throw new IOException("Destination appeared while installing; existing files retained.");
  }
  SafeFiles.WriteText(Path.Combine(stage,".alm-install.json"),JsonSerializer.Serialize(recipe,SettingsStore.Json));
  Directory.Move(stage,destination);return true;
 }
 static string Locate(string directory,string relative){
  if(string.IsNullOrWhiteSpace(relative))throw new InvalidDataException("Recipe has no unambiguous launch path.");
  var direct=SafeFiles.Child(directory,relative);if(File.Exists(direct))return direct;
  var normalized=relative.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar);
  var matches=SafeFiles.Files(directory,CancellationToken.None).Where(f=>f.EndsWith(Path.DirectorySeparatorChar+normalized,StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
  if(matches.Count==1)return matches[0];throw new InvalidDataException("Expected exactly one launch file: "+relative);
 }
}




