using System.Globalization;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed partial class LaunchBoxService {
 private sealed record ElfPair(GameRecord Legacy,GameRecord Elf2);
 private static bool IsDefaultMirror(IReadOnlyList<XElement> entries) {
  if(entries.Count!=2||entries.Count(e=>e.Name.LocalName=="Game")!=1||entries.Count(e=>e.Name.LocalName=="AdditionalApplication")!=1)return false;var game=entries.SingleOrDefault(e=>e.Name.LocalName=="Game");var app=entries.SingleOrDefault(e=>e.Name.LocalName=="AdditionalApplication");
  return game!=null&&app!=null&&Value(game,"ID").Equals(Value(app,"GameID"),StringComparison.OrdinalIgnoreCase)&&!Value(app,"AutoRunBefore").Equals("true",StringComparison.OrdinalIgnoreCase)&&!Value(app,"AutoRunAfter").Equals("true",StringComparison.OrdinalIgnoreCase);
 }
 private static ElfPair? LoaderPair(List<XElement>? entries,GameRecord candidate,IReadOnlyDictionary<string,GameRecord> catalog,AppSettings s) {
  if(entries==null||entries.Count==0)return null;
  var profiles=new Dictionary<string,GameRecord>(StringComparer.OrdinalIgnoreCase){{candidate.Id,candidate}};
  foreach(var entry in entries) {var id=ProfileId(entry,s.LaunchBoxPath,s.TeknoParrotPath);if(id==null||!catalog.TryGetValue(id,out var profile))return null;profiles[id]=profile;}
  if(profiles.Count!=2)return null;var elf2=profiles.Values.Where(GameSelectionPolicy.IsElfLoader2).ToList();var legacy=profiles.Values.Where(g=>!GameSelectionPolicy.IsElfLoader2(g)).ToList();
  return elf2.Count==1&&legacy.Count==1&&GameSelectionPolicy.LoaderFamilyKey(elf2[0].Name)==GameSelectionPolicy.LoaderFamilyKey(legacy[0].Name)?new(legacy[0],elf2[0]):null;
 }
 private static bool PlanLoaderCombination(AppSettings s,LaunchBoxPlan plan,GameRecord game,ElfPair pair,List<XElement> family,LaunchBoxPlanItem item,string profilePath) {
  var mains=family.Where(e=>e.Name.LocalName=="Game").ToList();
  bool Review(string detail){item.Action="NeedsReview";item.Detail=detail;item.Selected=false;return true;}
  if(!GameSelectionPolicy.IsElfLoader2(game))return mains.Count>1?Review("The ELF2 launch files are unavailable. Existing loader entries are preserved until ELF2 is ready."):false;
  if(family.Where(e=>e.Name.LocalName=="AdditionalApplication").Any(e=>Value(e,"AutoRunBefore").Equals("true",StringComparison.OrdinalIgnoreCase)||Value(e,"AutoRunAfter").Equals("true",StringComparison.OrdinalIgnoreCase)))return Review("A loader option is configured as an automatic launch hook; preserved for review.");
  if(mains.Count==0||mains.Count>2)return Review("The ELF loader pair has ambiguous parent game records; no records were combined.");
  var legacyGame=mains.SingleOrDefault(e=>ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath)?.Equals(pair.Legacy.Id,StringComparison.OrdinalIgnoreCase)==true);
  // Already combined: the ELF2 main entry plus its default mirror and legacy launch option is intentional.
  if(mains.Count==1&&legacyGame==null)return false;
  var retained=legacyGame!;var removed=mains.Where(e=>e!=retained).ToList();var retainedId=Value(retained,"ID");
  if(string.IsNullOrWhiteSpace(retainedId)||mains.Any(e=>string.IsNullOrWhiteSpace(Value(e,"ID"))))return Review("The loader pair has a missing game ID; preserved for review.");
  if(mains.Any(e=>!Value(e,"ApplicationPath").Trim('"').EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||!string.IsNullOrWhiteSpace(Value(e,"CommandLine"))||Value(e,"UseDosBox").Equals("true",StringComparison.OrdinalIgnoreCase)||Value(e,"UseScummVM").Equals("true",StringComparison.OrdinalIgnoreCase)))return Review("The loader pair uses a custom launch command or launch mode that cannot be merged safely; preserved for review.");
  var emulator=Value(retained,"Emulator");if(string.IsNullOrWhiteSpace(emulator)||mains.Any(e=>Value(e,"Emulator")!=emulator))return Review("The loader entries use different or missing emulator definitions; preserved for review.");
  var removedIds=removed.Select(e=>Value(e,"ID")).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var root=plan.Document.Root!;
  foreach(var app in root.Elements("AdditionalApplication").Where(e=>removedIds.Contains(Value(e,"GameID"))))
   if(Value(app,"AutoRunBefore").Equals("true",StringComparison.OrdinalIgnoreCase)||Value(app,"AutoRunAfter").Equals("true",StringComparison.OrdinalIgnoreCase))return Review("The secondary game has automatic launch hooks. Combining would change when they run, so its game and hooks are preserved for review.");
  if(mains.Any(main=>root.Elements("Game").Count(e=>Value(e,"ID").Equals(Value(main,"ID"),StringComparison.OrdinalIgnoreCase))!=1))return Review("The platform has colliding game IDs; preserved for review.");
  // Only direct GameID links on these two documented record types are reparented below.
  // Any other surviving element or attribute that references a removed ID needs human review.
  foreach(var node in root.Descendants()) {
   if(node.AncestorsAndSelf().Any(e=>e.Parent==root&&e.Name.LocalName=="Game"&&removedIds.Contains(Value(e,"ID"))))continue;
   if(node.Attributes().Any(a=>removedIds.Contains(a.Value)))return Review("An unsupported platform attribute references the secondary game ID; preserved for review.");
   if(node.HasElements||!removedIds.Contains(node.Value))continue;
   if(IsSupportedGameReference(node,root))continue;
   return Review("An unsupported platform field references the secondary game ID; preserved for review.");
  }

  if(ControllerSupportConflict(root,removedIds,retainedId) is string controllerIssue)return Review(controllerIssue);
  foreach(var field in new[]{"PlayCount","PlayTime"}) {
   ulong sum=0;foreach(var main in mains) {var text=Value(main,field);if(text.Length==0)continue;if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var amount)||ulong.MaxValue-sum<amount)return Review($"The loader pair has unsupported {field} history; preserved for review.");sum+=amount;}
   if(mains.Count>1)item.Changes[field]=sum.ToString(CultureInfo.InvariantCulture);
  }
  if(mains.Count>1) {
   item.Changes["Favorite"]=mains.Any(e=>Value(e,"Favorite").Equals("true",StringComparison.OrdinalIgnoreCase))?"true":"false";
   var dates=mains.Select(e=>Value(e,"LastPlayedDate")).Where(v=>v.Length>0).ToList();
   if(dates.Count>0) {var parsed=new List<(DateTimeOffset Date,string Text)>();foreach(var value in dates){if(!DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date))return Review("The loader pair has unsupported last-played history; preserved for review.");parsed.Add((date,value));}item.Changes["LastPlayedDate"]=parsed.OrderByDescending(x=>x.Date).First().Text;}
  }
  item.GameGuid=retainedId;item.ElementName="Game";item.ProfileHash=Hash(profilePath);item.Changes["ApplicationPath"]=Path.GetRelativePath(s.LaunchBoxPath,profilePath);item.Changes["Version"]=pair.Elf2.Id;
  item.DatabaseId=int.TryParse(Value(retained,"DatabaseID"),out var databaseId)&&databaseId>0?databaseId:null;
  item.RemovedGameIds.AddRange(removedIds);
  foreach(var member in new[]{pair.Elf2,pair.Legacy}) {
   var apps=family.Where(e=>e.Name.LocalName=="AdditionalApplication"&&ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath)?.Equals(member.Id,StringComparison.OrdinalIgnoreCase)==true).ToList();
   if(apps.Count>1)return Review("The loader pair has duplicate launch options; preserved for review.");
   if(apps.Count==1){var parent=Value(apps[0],"GameID");if(parent!=retainedId&&!removedIds.Contains(parent))return Review("A loader launch option belongs to a different parent game; preserved for review.");continue;}
   var source=mains.FirstOrDefault(e=>ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath)?.Equals(member.Id,StringComparison.OrdinalIgnoreCase)==true);
   var currentProfile=string.IsNullOrWhiteSpace(member.UserProfilePath)?Path.Combine(s.TeknoParrotPath,"UserProfiles",member.Id+".xml"):member.UserProfilePath;
   var memberPath=File.Exists(currentProfile)||source==null?Path.GetRelativePath(s.LaunchBoxPath,currentProfile):Value(source,"ApplicationPath");
   var app=new XElement("AdditionalApplication",new XElement("Id",Guid.NewGuid()),new XElement("PlayCount",0),new XElement("PlayTime",0),new XElement("GameID",retainedId),new XElement("ApplicationPath",memberPath),new XElement("AutoRunAfter",false),new XElement("AutoRunBefore",false),new XElement("CommandLine",""),new XElement("Name",$"Play {member.Id} Version..."),new XElement("UseDosBox",false),new XElement("UseEmulator",true),new XElement("WaitForExit",false),new XElement("Version",member.Id),new XElement("EmulatorId",emulator),new XElement("Priority",GameSelectionPolicy.IsElfLoader2(member)?1:2));
   foreach(var field in new[]{"ReleaseDate","Developer","Publisher","Status"})if(!string.IsNullOrWhiteSpace(Value(source,field)))app.SetElementValue(field,Value(source,field));
   item.AddedApplications.Add(app);
  }
  item.Action="Combine";item.Detail=$"Combine ELF loaders into one game with ELF2 as the default. Keep game ID {retainedId}, existing metadata and hooks; retain both launch choices. "+(removedIds.Count>0?"Merge play counts/time, favorite and last-played history; preserve the removed game's remaining fields in the automatic backup. Reattach its manual launch options and alternate names.":"No existing game ID is removed.");return true;
 }
 private static bool PlanFamilyConsolidation(AppSettings s,LaunchBoxPlan plan,GameRecord preferred,List<XElement> family,IReadOnlyDictionary<string,GameRecord> catalog,LaunchBoxPlanItem item,string profilePath) {
  bool Review(string detail){item.Action="NeedsReview";item.Detail=detail;item.Selected=false;return true;}
  var root=plan.Document.Root!;var mains=family.Where(e=>e.Name.LocalName=="Game").ToList();
  if(mains.Count==0)return Review("The regional launch choices belong to games outside this title family; preserved for review.");
  string? Id(XElement e)=>ProfileId(e,s.LaunchBoxPath,s.TeknoParrotPath);
  if(family.Any(e=>Id(e) is not string id||!catalog.ContainsKey(id)))return Review("An entry in this game family uses an unrecognized or custom launch identity; preserved for review.");
  if(mains.Any(e=>string.IsNullOrWhiteSpace(Value(e,"ID"))||root.Elements("Game").Count(other=>Value(other,"ID").Equals(Value(e,"ID"),StringComparison.OrdinalIgnoreCase))!=1))return Review("The game family has missing or colliding game IDs; preserved for review.");
  if(mains.Any(e=>!Value(e,"ApplicationPath").Trim('"').EndsWith(".xml",StringComparison.OrdinalIgnoreCase)||!string.IsNullOrWhiteSpace(Value(e,"CommandLine"))||Value(e,"UseDosBox").Equals("true",StringComparison.OrdinalIgnoreCase)||Value(e,"UseScummVM").Equals("true",StringComparison.OrdinalIgnoreCase)))return Review("The game family uses a custom launch command or launch mode that needs review before removing versions.");
  var parentIds=mains.Select(e=>Value(e,"ID")).ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(family.Where(e=>e.Name.LocalName=="AdditionalApplication").Any(e=>!parentIds.Contains(Value(e,"GameID"))))return Review("A regional launch option belongs to a different game family; its parent and launch choice are preserved for review.");
  var retained=mains.FirstOrDefault(e=>Id(e)!.Equals(preferred.Id,StringComparison.OrdinalIgnoreCase));
  if(retained==null) {
   var preferredApp=family.FirstOrDefault(e=>e.Name.LocalName=="AdditionalApplication"&&Id(e)!.Equals(preferred.Id,StringComparison.OrdinalIgnoreCase));
   retained=preferredApp!=null?mains.Single(e=>Value(e,"ID").Equals(Value(preferredApp,"GameID"),StringComparison.OrdinalIgnoreCase)):
    mains.OrderBy(e=>GameSelectionPolicy.RegionRank(catalog[Id(e)!].Name)).ThenBy(e=>GameSelectionPolicy.LoaderRank(catalog[Id(e)!])).ThenBy(e=>Value(e,"ID"),StringComparer.OrdinalIgnoreCase).First();
  }
  var retainedId=Value(retained,"ID");var emulator=Value(retained,"Emulator");
  if(string.IsNullOrWhiteSpace(emulator)||mains.Any(e=>!Value(e,"Emulator").Equals(emulator,StringComparison.OrdinalIgnoreCase)))return Review("The family uses different or missing emulator definitions; preserved for review.");
  var removedIds=mains.Where(e=>e!=retained).Select(e=>Value(e,"ID")).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var allApps=root.Elements("AdditionalApplication").Where(e=>parentIds.Contains(Value(e,"GameID"))).ToList();
  foreach(var app in allApps.Where(e=>removedIds.Contains(Value(e,"GameID"))))
   if(IsAutomatic(app))return Review("A game being removed has automatic launch hooks. Its game and hooks are preserved until their behavior is reviewed.");
  var legacyCandidates=GameSelectionPolicy.IsElfLoader2(preferred)?catalog.Values.Where(g=>!GameSelectionPolicy.IsElfLoader2(g)&&GameSelectionPolicy.LoaderFamilyKey(g.Name)==GameSelectionPolicy.LoaderFamilyKey(preferred.Name)&&ReadyProfile(ProfilePathFor(s,g),s.TeknoParrotPath)==null).ToList():new List<GameRecord>();
  // An exact loader pair may remain as alternate launch choices. Other locales/revisions are removed.
  if(legacyCandidates.Count>1)return Review("More than one original loader matches this exact region/revision; choose its legacy loader before consolidating.");
  var legacy=legacyCandidates.SingleOrDefault();var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){preferred.Id};if(legacy!=null)allowed.Add(legacy.Id);
  var familyApps=allApps.Where(e=>Id(e) is string id&&catalog.TryGetValue(id,out var record)&&GameSelectionPolicy.FamilyKey(record.Name)==GameSelectionPolicy.FamilyKey(preferred.Name)).ToList();
  if(familyApps.Any(IsAutomatic))return Review("A regional or loader launch option is configured as an automatic hook; preserved for review.");
  if(familyApps.Where(e=>allowed.Contains(Id(e)!)).Any(e=>!string.IsNullOrWhiteSpace(Value(e,"CommandLine"))||!Value(e,"UseEmulator").Equals("true",StringComparison.OrdinalIgnoreCase)&&Value(e,"UseEmulator").Length>0||Value(e,"EmulatorId").Length>0&&!Value(e,"EmulatorId").Equals(emulator,StringComparison.OrdinalIgnoreCase)))return Review("A retained loader choice has a custom launch configuration; preserved for review.");
  var keepApps=new Dictionary<string,XElement>(StringComparer.OrdinalIgnoreCase);
  foreach(var app in familyApps.Where(e=>allowed.Contains(Id(e)!)).OrderBy(e=>Value(e,"GameID").Equals(retainedId,StringComparison.OrdinalIgnoreCase)?0:1))keepApps.TryAdd(Id(app)!,app);
  var removedApps=familyApps.Where(e=>!allowed.Contains(Id(e)!)||keepApps[Id(e)!]!=e).ToList();
  if(removedApps.Any(e=>string.IsNullOrWhiteSpace(Value(e,"Id"))||root.Elements("AdditionalApplication").Count(other=>Value(other,"Id").Equals(Value(e,"Id"),StringComparison.OrdinalIgnoreCase))!=1))return Review("An extra launch option has a missing or colliding application ID; preserved for review.");
  var removedAppIds=removedApps.Select(e=>Value(e,"Id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(UnsupportedReferences(root,removedIds,removedAppIds))return Review("An unsupported platform field or attribute references an entry being removed; preserved for review.");
  if(ControllerSupportConflict(root,removedIds,retainedId) is string controllerIssue)return Review(controllerIssue);
  foreach(var field in new[]{"PlayCount","PlayTime"}) {
   ulong sum=0;foreach(var main in mains){var text=Value(main,field);if(text.Length==0)continue;if(!ulong.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var amount)||ulong.MaxValue-sum<amount)return Review($"The family has unsupported {field} history; preserved for review.");sum+=amount;}
   if(mains.Count>1)item.Changes[field]=sum.ToString(CultureInfo.InvariantCulture);
  }
  if(mains.Count>1) {
   item.Changes["Favorite"]=mains.Any(e=>Value(e,"Favorite").Equals("true",StringComparison.OrdinalIgnoreCase))?"true":"false";
   var dates=new List<(DateTimeOffset Date,string Text)>();foreach(var text in mains.Select(e=>Value(e,"LastPlayedDate")).Where(v=>v.Length>0)){if(!DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var date))return Review("The family has unsupported last-played history; preserved for review.");dates.Add((date,text));}
   if(dates.Count>0)item.Changes["LastPlayedDate"]=dates.OrderByDescending(x=>x.Date).First().Text;
  }
  item.GameGuid=retainedId;item.ElementName="Game";item.ProfileHash=Hash(profilePath);item.Changes["ApplicationPath"]=Path.GetRelativePath(s.LaunchBoxPath,profilePath);item.Changes["Version"]=preferred.Id;
  item.DatabaseId=int.TryParse(Value(retained,"DatabaseID"),out var databaseId)&&databaseId>0?databaseId:null;
  if(!Id(retained)!.Equals(preferred.Id,StringComparison.OrdinalIgnoreCase)&&Value(retained,"Title").Equals(catalog[Id(retained)!].Name,StringComparison.OrdinalIgnoreCase))item.Changes["Title"]=preferred.Name;
  item.RemovedGameIds.AddRange(removedIds);item.RemovedApplicationIds.AddRange(removedAppIds);
  if(legacy!=null)foreach(var member in new[]{preferred,legacy})if(!keepApps.ContainsKey(member.Id))item.AddedApplications.Add(CreateLoaderApplication(s,member,retainedId,emulator));
  item.Action="Consolidate";item.Detail=$"Keep one game: {preferred.Name} ({preferred.Id}), game ID {retainedId}. Back up and remove {removedIds.Count} extra game entries and {removedAppIds.Count} extra profile launch choices; ROMs and TeknoParrot profiles are unchanged. Merge play counts/time, favorite and last-played history; preserve/reparent manual tools and alternate names. "+(legacy!=null?"Keep the matching original ELF loader as an alternate, with ELF2 the default.":"Priority: USA, then World, then another region.");return true;
 }
 private static string ProfilePathFor(AppSettings s,GameRecord game)=>string.IsNullOrWhiteSpace(game.UserProfilePath)?Path.Combine(s.TeknoParrotPath,"UserProfiles",game.Id+".xml"):Path.GetFullPath(game.UserProfilePath);
 private static bool IsAutomatic(XElement app)=>Value(app,"AutoRunBefore").Equals("true",StringComparison.OrdinalIgnoreCase)||Value(app,"AutoRunAfter").Equals("true",StringComparison.OrdinalIgnoreCase);
 private static bool UnsupportedReferences(XElement root,ISet<string> removedGames,ISet<string> removedApps) {
  foreach(var node in root.Descendants()) {
   if(node.AncestorsAndSelf().Any(e=>e.Parent==root&&(e.Name.LocalName=="Game"&&removedGames.Contains(Value(e,"ID"))||e.Name.LocalName=="AdditionalApplication"&&removedApps.Contains(Value(e,"Id")))))continue;
   if(node.Attributes().Any(a=>removedGames.Contains(a.Value)||removedApps.Contains(a.Value)))return true;
   if(node.HasElements)continue;
   if(removedApps.Contains(node.Value))return true;
   if(!removedGames.Contains(node.Value))continue;
   if(IsSupportedGameReference(node,root))continue;
   return true;
  }
  return false;
 }
 private static bool IsSupportedGameReference(XElement field,XElement root) => field.Parent?.Parent==root&&
  (field.Name.LocalName=="GameID"&&field.Parent.Elements("GameID").Count()==1&&field.Parent.Name.LocalName is "AdditionalApplication" or "AlternateName"||field.Name.LocalName=="GameId"&&field.Parent.Name.LocalName=="GameControllerSupport"&&field.Parent.Elements("GameId").Count()==1);
 private static string? ControllerSupportConflict(XElement root,ISet<string> removedIds,string retainedId) {
  if(removedIds.Count==0)return null;
  var rows=root.Elements("GameControllerSupport").Where(e=>removedIds.Contains(Value(e,"GameId"))||Value(e,"GameId").Equals(retainedId,StringComparison.OrdinalIgnoreCase)).ToList();
  foreach(var row in rows)foreach(var field in new[]{"GameId","ControllerId","SupportLevel"})
   if(row.Elements(field).Count()!=1||row.Element(field)!.HasElements||string.IsNullOrWhiteSpace(Value(row,field)))return "A game controller support record has an unsupported or missing key/value; preserved for review.";
  foreach(var group in rows.GroupBy(e=>Value(e,"ControllerId"),StringComparer.OrdinalIgnoreCase).Where(g=>g.Any(e=>removedIds.Contains(Value(e,"GameId"))))) {
   var keep=group.FirstOrDefault(e=>Value(e,"GameId").Equals(retainedId,StringComparison.OrdinalIgnoreCase))??group.First();
   foreach(var other in group.Where(e=>e!=keep)) {
    if(Value(other,"SupportLevel")!=Value(keep,"SupportLevel"))return $"Controller {group.Key} has conflicting support levels across these game entries; preserved for review.";
    if(!XNode.DeepEquals(ComparableControllerSupport(keep),ComparableControllerSupport(other)))return $"Controller {group.Key} has different custom support settings across these game entries; preserved for review.";
   }
  }
  return null;
 }
 private static XElement ComparableControllerSupport(XElement row) {
  var copy=new XElement(row);copy.SetElementValue("GameId","retained-game");copy.SetElementValue("ControllerId",Value(copy,"ControllerId").ToUpperInvariant());
  var document=new XDocument(copy);NormalizeFormatting(document);return copy;
 }
 private static void ApplyControllerSupport(XElement root,ISet<string> removedIds,string retainedId) {
  if(removedIds.Count==0)return;
  var groups=root.Elements("GameControllerSupport").Where(e=>removedIds.Contains(Value(e,"GameId"))||Value(e,"GameId").Equals(retainedId,StringComparison.OrdinalIgnoreCase))
   .GroupBy(e=>Value(e,"ControllerId"),StringComparer.OrdinalIgnoreCase).Where(g=>g.Any(e=>removedIds.Contains(Value(e,"GameId")))).Select(g=>g.ToList()).ToList();
  foreach(var group in groups) {
   var keep=group.FirstOrDefault(e=>Value(e,"GameId").Equals(retainedId,StringComparison.OrdinalIgnoreCase))??group[0];keep.SetElementValue("GameId",retainedId);
   foreach(var duplicate in group.Where(e=>e!=keep))duplicate.Remove();
  }
 }
 private static XElement CreateLoaderApplication(AppSettings s,GameRecord profile,string parentId,string emulator) =>
  new("AdditionalApplication",new XElement("Id",Guid.NewGuid()),new XElement("PlayCount",0),new XElement("PlayTime",0),new XElement("GameID",parentId),new XElement("ApplicationPath",Path.GetRelativePath(s.LaunchBoxPath,ProfilePathFor(s,profile))),new XElement("AutoRunAfter",false),new XElement("AutoRunBefore",false),new XElement("CommandLine",""),new XElement("Name",$"Play {profile.Id} Version..."),new XElement("UseDosBox",false),new XElement("UseEmulator",true),new XElement("WaitForExit",false),new XElement("Version",profile.Id),new XElement("EmulatorId",emulator),new XElement("Priority",GameSelectionPolicy.IsElfLoader2(profile)?1:2));
 private static void ApplyCombination(XDocument document,LaunchBoxPlanItem item) {
  var removed=item.RemovedGameIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
  foreach(var entry in document.Root!.Elements("Game").Where(e=>removed.Contains(Value(e,"ID"))).ToList())entry.Remove();
  var removedApps=item.RemovedApplicationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);foreach(var app in document.Root.Elements("AdditionalApplication").Where(e=>removedApps.Contains(Value(e,"Id"))).ToList())app.Remove();
  foreach(var node in document.Root.Elements().Where(e=>e.Name.LocalName is "AdditionalApplication" or "AlternateName"))if(removed.Contains(Value(node,"GameID")))node.SetElementValue("GameID",item.GameGuid);
  ApplyControllerSupport(document.Root,removed,item.GameGuid);
  foreach(var app in item.AddedApplications)document.Root.Add(new XElement(app));
 }
}