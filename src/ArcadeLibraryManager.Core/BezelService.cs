using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;
public sealed class BezelPlanItem {
 public bool Selected{get;set;}=true;
 public string ProfileId{get;init;}="";public string Name{get;init;}="";public string Action{get;internal set;}="";public string Detail{get;internal set;}="";public string Source{get;internal set;}="";public string Destination{get;internal set;}="";
 internal string SourceSetFingerprint{get;set;}="";internal string ArchiveMember{get;set;}="";internal string SourceHash{get;set;}="";internal string ProfilePath{get;set;}="";internal string ProfileHash{get;set;}="";internal string TemplatePath{get;set;}="";internal string TemplateHash{get;set;}="";internal string? DestinationHash{get;set;}
 internal Dictionary<string,string> Fields{get;}=new(StringComparer.OrdinalIgnoreCase);
}
public sealed class BezelPlan {
 public List<BezelPlanItem> Items{get;}=new();public List<string> BackupPaths{get;}=new();public bool CanApply=>Items.Any(i=>i.Selected&&i.Action is "Install" or "Enable");internal string TeknoParrotPath{get;set;}="";internal string BackupRoot{get;set;}="";internal string MameArtworkPath{get;set;}="";internal string RepositoryPath{get;set;}="";
}
public sealed record BezelRollbackEntry(string Path,string? Backup,string? BeforeHash,string AfterHash);
public sealed record BezelRollbackJournal(List<BezelRollbackEntry> Entries,DateTimeOffset CommittedUtc);
/// <summary>Copies verified, unambiguous alpha overlays to destinations documented in the current TeknoParrot profile.</summary>
public sealed class BezelService {
 private readonly Func<bool> running;
 private readonly VolumeHealthService volumeHealth;
 private static readonly SemaphoreSlim Gate=new(1,1);
 public BezelService(Func<bool>? processRunning=null,VolumeHealthService? healthService=null){volumeHealth=healthService??new();running=processRunning??(()=>Process.GetProcesses().Any(p=>{using(p){return p.ProcessName.StartsWith("Tekno",StringComparison.OrdinalIgnoreCase)||p.ProcessName.StartsWith("OpenParrot",StringComparison.OrdinalIgnoreCase);}}));}
 private static string V(XElement? e,string name)=>e?.Element(name)?.Value??"";
 private static IEnumerable<XElement> Fields(XElement root)=>root.Element("ConfigValues")?.Elements("FieldInformation")??Enumerable.Empty<XElement>();
 private static string Key(XElement e)=>V(e,"CategoryName")+"\n"+V(e,"FieldName");
 private static bool On(string v)=>v=="1"||v.Equals("true",StringComparison.OrdinalIgnoreCase);
 private static string BoolValue(string old,bool enabled)=>old.Equals("true",StringComparison.OrdinalIgnoreCase)||old.Equals("false",StringComparison.OrdinalIgnoreCase)?enabled?"true":"false":enabled?"1":"0";
 public Task<BezelPlan> PreviewAsync(AppSettings s,IEnumerable<GameRecord> games,IProgress<JobEvent>? progress=null,CancellationToken ct=default)=>Task.Run(()=>{
  var selectedGames=games.ToList();
  var sources=BezelSourceIndex.Create(s.MameArtworkPath,s.BezelImportPath,selectedGames.Select(g=>g.Id),ct);
  var plan=new BezelPlan{TeknoParrotPath=Path.GetFullPath(s.TeknoParrotPath),BackupRoot=Path.Combine(s.TeknoParrotPath,"Backups","ArcadeLibraryManager","Bezels"),MameArtworkPath=s.MameArtworkPath,RepositoryPath=s.BezelImportPath};
  foreach(var game in selectedGames){ct.ThrowIfCancellationRequested();var item=new BezelPlanItem{ProfileId=game.Id,Name=game.Name,SourceSetFingerprint=sources.Fingerprint(game.Id),ProfilePath=string.IsNullOrWhiteSpace(game.UserProfilePath)?Path.Combine(s.TeknoParrotPath,"UserProfiles",game.Id+".xml"):game.UserProfilePath,TemplatePath=string.IsNullOrWhiteSpace(game.TemplatePath)?Path.Combine(s.TeknoParrotPath,"GameProfiles",game.Id+".xml"):game.TemplatePath};plan.Items.Add(item);
   try{
    if(!File.Exists(item.ProfilePath)||!File.Exists(item.TemplatePath)){Decline(item,"NotReady","Install/register this game first.");continue;}
    var user=LaunchBoxService.ReadXml(item.ProfilePath).Root!;var template=LaunchBoxService.ReadXml(item.TemplatePath).Root!;item.ProfileHash=LaunchBoxService.Hash(item.ProfilePath);item.TemplateHash=LaunchBoxService.Hash(item.TemplatePath);
    var enable=Fields(template).Where(e=>V(e,"FieldName").Equals("Use Bezel",StringComparison.OrdinalIgnoreCase)||(V(e,"CategoryName").Equals("Bezel",StringComparison.OrdinalIgnoreCase)&&new[]{"Enable","Activate","Enabled"}.Contains(V(e,"FieldName"),StringComparer.OrdinalIgnoreCase))).ToList();
    if(enable.Count!=1){Decline(item,"Unsupported","This profile does not document a unique bezel enable setting.");continue;}
    var field=enable[0];var hint=V(field,"Hint");var primary=V(user,"GamePath");if(string.IsNullOrWhiteSpace(primary)||!File.Exists(LaunchBoxService.FullPath(s.TeknoParrotPath,primary))){Decline(item,"NotReady","The primary game file is missing.");continue;}
    var module=Regex.Match(hint,@"\b(Tekno[A-Za-z0-9]+)/?\\?bezels\b",RegexOptions.IgnoreCase);if(!module.Success)module=Regex.Match(hint,@"bezels folder in the (Tekno[A-Za-z0-9]+) folder",RegexOptions.IgnoreCase);
    if(module.Success)item.Destination=SafeFiles.Child(s.TeknoParrotPath,module.Groups[1].Value+"/bezels/"+game.Id+".png");
    else if(hint.Contains("bezel.png",StringComparison.OrdinalIgnoreCase)&&(hint.Contains("game folder",StringComparison.OrdinalIgnoreCase)||hint.Contains("game executable",StringComparison.OrdinalIgnoreCase)))item.Destination=SafeFiles.Child(Path.GetDirectoryName(LaunchBoxService.FullPath(s.TeknoParrotPath,primary))!,"bezel.png");
    else{Decline(item,"Unsupported","The current profile does not document a safe bezel destination; no path guessed.");continue;}
    var existing=Fields(user).SingleOrDefault(e=>Key(e).Equals(Key(field),StringComparison.OrdinalIgnoreCase));if(existing==null){Decline(item,"NeedsReview","The installed profile is missing its current bezel setting; update the profile first.");continue;}
    item.Fields[Key(existing)]=BoolValue(V(existing,"FieldValue"),true);
    foreach(var f in Fields(user)) {
     var name=V(f,"FieldName");if(name.Equals("DisplayMode",StringComparison.OrdinalIgnoreCase)&&f.Element("FieldOptions")?.Elements("string").Any(x=>x.Value=="Fullscreen")==true)item.Fields[Key(f)]="Fullscreen";
     if(name.Equals("Windowed",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Stretch to Fullscreen",StringComparison.OrdinalIgnoreCase))item.Fields[Key(f)]=BoolValue(V(f,"FieldValue"),false);
    }
    foreach(var k in item.Fields.Keys.ToList())if(V(Fields(user).Single(f=>Key(f).Equals(k,StringComparison.OrdinalIgnoreCase)),"FieldValue")==item.Fields[k])item.Fields.Remove(k);
    if(File.Exists(item.Destination)) {ValidatePng(File.ReadAllBytes(item.Destination));item.DestinationHash=LaunchBoxService.Hash(item.Destination);item.Source=item.Destination;item.SourceHash=item.DestinationHash;item.Action=item.Fields.Count>0?"Enable":"Unchanged";item.Detail=item.Fields.Count>0?"Keep existing bezel; enable it and use fullscreen without stretch. Controls preserved.":"Existing bezel and enabled display settings preserved.";item.Selected=item.Action=="Enable";continue;}
    string? source=null,sourceIssue=null;PngInfo? selectedPng=null;string selectedMember="";
    foreach(var provider in new[]{(Name:"MAME artwork",Candidates:sources.Mame(game.Id),Issue:sources.MameIssue(game.Id)),(Name:"local bezel repository",Candidates:sources.Repository(game.Id),Issue:sources.RepositoryIssue)}) {
     if(provider.Issue.Length>0){sourceIssue=provider.Issue;continue;}
     if(provider.Candidates.Count>1){sourceIssue=$"Multiple exact bezel packages exist in {provider.Name}; keep one chosen source for {game.Id}.";break;}
     if(provider.Candidates.Count==1) {try{var bytes=ReadSource(provider.Candidates[0],out var member);selectedPng=ValidatePng(bytes);selectedMember=member;source=provider.Candidates[0];break;}catch(Exception ex)when(ex is IOException or InvalidDataException or XmlException){sourceIssue=ex.Message;}}
    }
    if(source==null){Decline(item,sourceIssue==null?"Missing":"NeedsReview",sourceIssue??("No exact local bezel found. Choose a local bezel repository containing "+game.Id+".png or .zip, or a "+game.Id+" folder containing bezel.png. Subfolders are searched; .git and linked folders are skipped."));continue;}
    item.Source=source;item.SourceHash=LaunchBoxService.Hash(source);item.ArchiveMember=selectedMember;item.Action="Install";item.Detail=$"Install {selectedPng!.Width}×{selectedPng.Height} alpha overlay; enable bezel, fullscreen and no stretch. Controls preserved. Check the visual fit when playing.";
   }catch(Exception ex)when(ex is IOException or InvalidDataException or XmlException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OverflowException){Decline(item,"NeedsReview",ex.Message);}
  }
  // Shared executable directories must not receive two different overlays.
  foreach(var group in plan.Items.Where(i=>i.Action=="Install").GroupBy(i=>i.Destination,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))foreach(var item in group)Decline(item,"NeedsReview","Multiple games share this bezel destination; choose a shared overlay manually.");
  progress?.Report(new("Bezels",$"Preview: {plan.Items.Count(i=>i.Action=="Install")} overlays, {plan.Items.Count(i=>i.Action=="Enable")} existing overlays to enable."));return plan;
 },ct);
 private static void Decline(BezelPlanItem i,string action,string detail){i.Action=action;i.Detail=detail;i.Selected=false;}
 private static byte[] ReadSource(string path,out string member) {
  member="";if(new FileInfo(path).Length>256L*1024*1024)throw new InvalidDataException("Bezel package exceeds 256 MB; choose a single overlay.");if(Path.GetExtension(path).Equals(".png",StringComparison.OrdinalIgnoreCase))return File.ReadAllBytes(path);
  using var zip=ZipFile.OpenRead(path);foreach(var e in zip.Entries){SafeFiles.Child(Path.GetTempPath(),e.FullName.TrimEnd('/','\\'));if(((e.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Linked archive entries are not supported.");}
  var images=zip.Entries.Where(e=>e.Name.EndsWith(".png",StringComparison.OrdinalIgnoreCase)).ToList();if(images.Count!=1)throw new InvalidDataException("MAME artwork has multiple image layers/views; export a single composed transparent PNG for review.");
  var chosen=images[0];if(chosen.Length>128L*1024*1024)throw new InvalidDataException("Bezel PNG exceeds 128 MB.");
  foreach(var layout in zip.Entries.Where(e=>e.Name.EndsWith(".lay",StringComparison.OrdinalIgnoreCase))) {
   if(layout.Length>1024*1024)throw new InvalidDataException("MAME layout is unexpectedly large.");using var input=layout.Open();using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});var doc=XDocument.Load(reader);var views=doc.Descendants("view").ToList();var imagesInLayout=doc.Descendants("image").ToList();
   if(views.Count!=1||views[0].Descendants("screen").Count()!=1||imagesInLayout.Count!=1||doc.Descendants().Any(e=>e.Name.LocalName is "group" or "repeat" or "orientation" or "color" or "script")||!Path.GetFileName((string?)imagesInLayout[0].Attribute("file")??"").Equals(chosen.Name,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("MAME layout requires composition, rotation or multiple views; export a single transparent PNG instead.");
  }
  using var stream=chosen.Open();using var output=new MemoryStream((int)chosen.Length);stream.CopyTo(output);var bytes=output.ToArray();if(bytes.LongLength!=chosen.Length||~Crc32.Update(0xffffffff,bytes)!=chosen.Crc32)throw new InvalidDataException("Bezel archive CRC mismatch.");member=chosen.FullName;return bytes;
 }
 public async Task<OperationResult> ApplyAsync(BezelPlan plan,IProgress<JobEvent>? progress=null,CancellationToken ct=default) {
  await Gate.WaitAsync(ct);try{if(running())throw new InvalidOperationException("Close TeknoParrot and its games before changing bezel profiles.");var errors=new List<string>();var selected=plan.Items.Where(i=>i.Selected&&i.Action is "Install" or "Enable").ToList();int succeeded=0;
   var currentSources=BezelSourceIndex.Create(plan.MameArtworkPath,plan.RepositoryPath,selected.Select(i=>i.ProfileId),ct);
   if(selected.Count>0)await volumeHealth.EnsureWritableAsync(selected.SelectMany(i=>new[]{i.ProfilePath,i.Destination}).Append(plan.BackupRoot),ct);
   foreach(var item in selected){ct.ThrowIfCancellationRequested();try{
    if(item.Action=="Install"&&currentSources.Fingerprint(item.ProfileId)!=item.SourceSetFingerprint)throw new InvalidOperationException("The exact bezel source candidates changed after preview; review the repository again before installing.");
    if(LaunchBoxService.Hash(item.ProfilePath)!=item.ProfileHash||LaunchBoxService.Hash(item.TemplatePath)!=item.TemplateHash||LaunchBoxService.Hash(item.Source)!=item.SourceHash)throw new InvalidOperationException("Profile, template or source changed after preview; scan again.");
    if(item.DestinationHash==null&&File.Exists(item.Destination)||item.DestinationHash!=null&&(!File.Exists(item.Destination)||LaunchBoxService.Hash(item.Destination)!=item.DestinationHash))throw new InvalidOperationException("Destination changed after preview; scan again.");
    if(running())throw new InvalidOperationException("TeknoParrot opened during the operation; close it before continuing.");
    var doc=LaunchBoxService.ReadXml(item.ProfilePath);foreach(var pair in item.Fields){var field=Fields(doc.Root!).Single(f=>Key(f).Equals(pair.Key,StringComparison.OrdinalIgnoreCase));field.SetElementValue("FieldValue",pair.Value);}
    var stamp=DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8];var backup=SafeFiles.Child(plan.BackupRoot,stamp);Directory.CreateDirectory(backup);var profileBackup=Path.Combine(backup,Path.GetFileName(item.ProfilePath));var profileTemp=item.ProfilePath+".alm-"+Guid.NewGuid().ToString("N")+".tmp";var imageTemp=item.Destination+".alm-"+Guid.NewGuid().ToString("N")+".tmp";
    var entries=new List<BezelRollbackEntry>();bool copied=false,profileCommitted=false;
    try{doc.Save(profileTemp);LaunchBoxService.ReadXml(profileTemp);entries.Add(new(item.ProfilePath,profileBackup,item.ProfileHash,LaunchBoxService.Hash(profileTemp)));
     if(item.Action=="Install"){var bytes=ReadSource(item.Source,out var member);if(member!=item.ArchiveMember)throw new InvalidOperationException("Archive contents changed.");ValidatePng(bytes);Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);await File.WriteAllBytesAsync(imageTemp,bytes,ct);entries.Add(new(item.Destination,null,null,LaunchBoxService.Hash(imageTemp)));}
     ct.ThrowIfCancellationRequested();if(LaunchBoxService.Hash(item.ProfilePath)!=item.ProfileHash)throw new InvalidOperationException("Profile changed during staging.");
     var journal=Path.Combine(backup,"rollback.json");File.WriteAllText(journal,JsonSerializer.Serialize(new BezelRollbackJournal(entries,DateTimeOffset.UtcNow),SettingsStore.Json));
     if(item.Action=="Install"){File.Move(imageTemp,item.Destination,false);copied=true;}
     File.Replace(profileTemp,item.ProfilePath,profileBackup,true);profileCommitted=true;plan.BackupPaths.Add(journal);succeeded++;progress?.Report(new("Bezels",item.Name+": bezel installed/enabled; controls preserved.",item.ProfileId));
    }catch{if(copied&&!profileCommitted&&File.Exists(item.Destination)&&LaunchBoxService.Hash(item.Destination)==entries.Last().AfterHash)File.Delete(item.Destination);throw;}
    finally{if(File.Exists(profileTemp))File.Delete(profileTemp);if(File.Exists(imageTemp))File.Delete(imageTemp);}
   }catch(OperationCanceledException){throw;}catch(Exception ex){errors.Add(item.Name+": "+ex.Message);progress?.Report(new("Bezels",item.Name+": "+ex.Message,item.ProfileId));}}
   return new(succeeded,plan.Items.Count-selected.Count,errors);
  }finally{Gate.Release();}
 }
 public async Task RollbackAsync(string journalPath,CancellationToken ct=default){await Gate.WaitAsync(ct);try{if(running())throw new InvalidOperationException("Close TeknoParrot before rollback.");var journal=JsonSerializer.Deserialize<BezelRollbackJournal>(await File.ReadAllTextAsync(journalPath,ct))??throw new InvalidDataException("Invalid bezel rollback journal.");await volumeHealth.EnsureWritableAsync(journal.Entries.Select(e=>e.Path),ct);foreach(var e in journal.Entries){if(!File.Exists(e.Path)||LaunchBoxService.Hash(e.Path)!=e.AfterHash||e.Backup!=null&&LaunchBoxService.Hash(e.Backup)!=e.BeforeHash)throw new InvalidOperationException("A file changed since this operation; rollback was not applied.");}foreach(var e in journal.Entries){if(e.Backup==null)File.Delete(e.Path);else{var tmp=e.Path+".rollback-"+Guid.NewGuid().ToString("N");File.Copy(e.Backup,tmp);File.Replace(tmp,e.Path,null,true);}}}finally{Gate.Release();}}
 private sealed record PngInfo(int Width,int Height);
 private static PngInfo ValidatePng(byte[] bytes) {
  if(bytes.Length<33||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("Bezel is not a PNG.");
  int width=0,height=0,color=0,depth=0,interlace=0;bool headerSeen=false,endSeen=false,paletteSeen=false;byte[]? transparency=null;using var compressed=new MemoryStream();int at=8;
  while(at+12<=bytes.Length){var length=checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at,4)));if(length<0||length>128*1024*1024||at+12L+length>bytes.Length)throw new InvalidDataException("Invalid PNG chunk.");var type=Encoding.ASCII.GetString(bytes,at+4,4);var data=bytes.AsSpan(at+8,length);var expected=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at+8+length,4));if(~Crc32.Update(0xffffffff,bytes.AsSpan(at+4,length+4))!=expected)throw new InvalidDataException("PNG checksum mismatch.");
   if(type=="IHDR"){if(headerSeen||at!=8)throw new InvalidDataException("Invalid PNG header order.");headerSeen=true;if(length!=13)throw new InvalidDataException("Invalid PNG header.");width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));height=checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));depth=data[8];color=data[9];interlace=data[12];}
   else if(type=="PLTE")paletteSeen=true;else if(type=="tRNS")transparency=data.ToArray();else if(type=="IDAT"){if(!headerSeen)throw new InvalidDataException("PNG image data precedes header.");compressed.Write(data);}else if(type=="IEND"){endSeen=true;break;}at+=length+12;
  }
  if(!headerSeen||!endSeen||compressed.Length==0)throw new InvalidDataException("Incomplete PNG image.");
  if(width<16||height<16||width>8192||height>8192||(long)width*height>40_000_000||depth!=8||interlace!=0||color is not (3 or 4 or 6))throw new InvalidDataException("Use a non-interlaced 8-bit alpha PNG (RGBA, grayscale-alpha or indexed-alpha), at most 8192 pixels per edge.");
  if(color==3&&(transparency==null||!paletteSeen))throw new InvalidDataException("Indexed PNG has no transparency.");int channels=color==6?4:color==4?2:1;var previous=new byte[checked(width*channels)];var row=new byte[previous.Length];long transparent=0,opaque=0;bool center=false;compressed.Position=0;using var zlib=new ZLibStream(compressed,CompressionMode.Decompress);
  for(int y=0;y<height;y++){int filter=zlib.ReadByte();if(filter<0||filter>4)throw new InvalidDataException("Invalid PNG scanline.");zlib.ReadExactly(row);for(int x=0;x<row.Length;x++){int left=x>=channels?row[x-channels]:0,up=previous[x],ul=x>=channels?previous[x-channels]:0;int predictor=filter switch{0=>0,1=>left,2=>up,3=>(left+up)/2,4=>Paeth(left,up,ul),_=>0};row[x]=unchecked((byte)(row[x]+predictor));}for(int x=0;x<width;x++){var alpha=color==3?(row[x]<transparency!.Length?transparency[row[x]]:255):row[x*channels+channels-1];if(alpha<=16)transparent++;if(alpha>=240)opaque++;if(x==width/2&&y==height/2)center=alpha<=16;}(row,previous)=(previous,row);}
  if(zlib.ReadByte()!=-1)throw new InvalidDataException("Unexpected PNG image data.");if(!center||transparent<width*(long)height/20||opaque<width*(long)height/100)throw new InvalidDataException("PNG needs a transparent game opening at its center and visible bezel artwork; opaque backgrounds are not overlays.");return new(width,height);
 }
 private static int Paeth(int a,int b,int c){int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;}
}



