using ArcadeLibraryManager.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
public static class BezelChecks {
 static void Check(bool value,string reason){if(!value)throw new Exception("Bezel check failed: "+reason);}
 public static async Task RunAsync(string root) {
  var work=Path.Combine(root,"bezels-"+Guid.NewGuid().ToString("N"));var tp=Path.Combine(work,"TeknoParrot");var mame=Path.Combine(work,"MAME","artwork");var imports=Path.Combine(work,"imports");Directory.CreateDirectory(Path.Combine(tp,"GameProfiles"));Directory.CreateDirectory(Path.Combine(tp,"UserProfiles"));Directory.CreateDirectory(mame);Directory.CreateDirectory(imports);
  var settings=new AppSettings{TeknoParrotPath=tp,MameArtworkPath=mame,BezelImportPath=imports};var games=new List<GameRecord>();
  XElement Field(string category,string name,string value,string hint="")=>new("FieldInformation",new XElement("CategoryName",category),new XElement("FieldName",name),new XElement("FieldValue",value),new XElement("Hint",hint));
  foreach(var id in new[]{"good","complex","unsafe","fallback","opaque"}){var file=Path.Combine(work,id+".zip");File.WriteAllBytes(file,new byte[]{1});var doc=new XDocument(new XElement("GameProfile",new XElement("GamePath",file),new XElement("EmulatorType","TeknoS11"),new XElement("Controls",new XElement("Button","Space")),new XElement("ConfigValues",Field("Video","Use Bezel","0",$"Fullscreen overlay: TeknoS11/bezels/{id}.png or default.png. Use transparent pixels."),Field("Video","Stretch to Fullscreen","1"),Field("General","Windowed","1"),Field("General","Input API","RawInput"))));var template=Path.Combine(tp,"GameProfiles",id+".xml");var user=Path.Combine(tp,"UserProfiles",id+".xml");doc.Save(template);doc.Save(user);games.Add(new(){Id=id,Name=id,TemplatePath=template,UserProfilePath=user});}
  var png=Png(false);var noAlpha=Png(true);string layout="<mamelayout version='2'><element name='bezel'><image file='bezel.png'/></element><view name='single'><screen index='0'><bounds x='0' y='0' width='1' height='1'/></screen><bezel element='bezel'><bounds x='0' y='0' width='1' height='1'/></bezel></view></mamelayout>";
  void Package(string id,bool complex=false,bool unsafePath=false){using var zip=ZipFile.Open(Path.Combine(mame,id+".zip"),ZipArchiveMode.Create);using(var output=zip.CreateEntry("bezel.png").Open())output.Write(png);using(var writer=new StreamWriter(zip.CreateEntry("default.lay").Open()))writer.Write(complex?layout.Replace("</mamelayout>","<view name='extra'><screen index='0'/></view></mamelayout>"):layout);if(unsafePath){using var output=zip.CreateEntry("../escape.png").Open();output.Write(png);}}
  Package("good");Package("complex",true);Package("unsafe",unsafePath:true);Package("fallback",true);File.WriteAllBytes(Path.Combine(imports,"fallback.png"),png);File.WriteAllBytes(Path.Combine(mame,"opaque.png"),noAlpha);
  var running=false;var service=new BezelService(()=>running,VolumeHealthChecks.CleanProvider());var plan=await service.PreviewAsync(settings,games);Check(plan.Items.Single(i=>i.ProfileId=="good").Action=="Install","single safe alpha overlay ready");Check(plan.Items.Single(i=>i.ProfileId=="complex").Action=="NeedsReview","complex MAME layouts declined");Check(plan.Items.Single(i=>i.ProfileId=="unsafe").Action=="NeedsReview","traversal archive declined");Check(plan.Items.Single(i=>i.ProfileId=="opaque").Action=="NeedsReview","opaque PNG declined");Check(plan.Items.Single(i=>i.ProfileId=="fallback").Source==Path.Combine(imports,"fallback.png"),"compatible imported fallback selected");
  var originals=games.ToDictionary(g=>g.Id,g=>File.ReadAllBytes(g.UserProfilePath));running=true;try{await service.ApplyAsync(plan);throw new Exception("Expected open-process rejection");}catch(InvalidOperationException){}running=false;
  var blockedService=new BezelService(()=>false,new VolumeHealthService((path,ct)=>Task.FromResult(new VolumeHealthResult(path,"fixture","NTFS",VolumeHealthState.Dirty,"Test"))));try{await blockedService.ApplyAsync(plan);throw new Exception("Dirty bezel commit accepted");}catch(VolumeHealthException){}Check(!Directory.Exists(Path.Combine(tp,"TeknoS11")),"dirty guard precedes bezel writes");
  var result=await service.ApplyAsync(plan);Check(result.Succeeded==2&&result.Errors.Count==0,"safe overlays committed");Check(File.Exists(Path.Combine(tp,"TeknoS11","bezels","good.png")),"profile-documented destination used");Check(!File.Exists(Path.Combine(tp,"TeknoS11","escape.png")),"no traversal output");
  var userDoc=XDocument.Load(games[0].UserProfilePath);Check(userDoc.Root!.Element("Controls")!.Element("Button")!.Value=="Space","controls preserved");var fields=userDoc.Descendants("FieldInformation").ToDictionary(e=>e.Element("FieldName")!.Value,e=>e.Element("FieldValue")!.Value);Check(fields["Use Bezel"]=="1"&&fields["Windowed"]=="0"&&fields["Stretch to Fullscreen"]=="0","bezel enabled with compatible display settings");Check(fields["Input API"]=="RawInput","unrelated settings preserved");
  var repeat=await service.PreviewAsync(settings,games);Check(!repeat.CanApply,"idempotent reuse");foreach(var journal in plan.BackupPaths)await service.RollbackAsync(journal);Check(!File.Exists(Path.Combine(tp,"TeknoS11","bezels","good.png")),"rollback removes only owned overlay");foreach(var g in games)Check(File.ReadAllBytes(g.UserProfilePath).SequenceEqual(originals[g.Id]),"original profile restored exactly");
  var stale=await service.PreviewAsync(settings,games);File.AppendAllText(games[0].UserProfilePath,"\n");var staleResult=await service.ApplyAsync(stale);Check(staleResult.Errors.Any(e=>e.Contains("changed after preview")),"concurrent profile edit rejected per item");
  await RepositoryChecksAsync(Path.Combine(work,"repository-checks"));
  Console.WriteLine("Bezel checks passed: alpha PNG inspection, safe ZIP, complex layout refusal, recursive exact repository discovery, source precedence and ambiguity, stale source sets, preserved controls, display enable, cancellation, idempotency and rollback.");
 }
 static async Task RepositoryChecksAsync(string work) {
  var tp=Path.Combine(work,"TeknoParrot");var mame=Path.Combine(work,"MAME","artwork");var imports=Path.Combine(work,"repository");
  Directory.CreateDirectory(Path.Combine(tp,"GameProfiles"));Directory.CreateDirectory(Path.Combine(tp,"UserProfiles"));Directory.CreateDirectory(mame);Directory.CreateDirectory(imports);
  var settings=new AppSettings{TeknoParrotPath=tp,MameArtworkPath=mame,BezelImportPath=imports};var service=new BezelService(()=>false,VolumeHealthChecks.CleanProvider());var png=Png(false);
  GameRecord Game(string id) {
   var executable=Path.Combine(work,id+".exe");File.WriteAllBytes(executable,new byte[]{1});
   XElement Field(string name,string value,string hint="")=>new("FieldInformation",new XElement("CategoryName","Video"),new XElement("FieldName",name),new XElement("FieldValue",value),new XElement("Hint",hint));
   var doc=new XDocument(new XElement("GameProfile",new XElement("GamePath",executable),new XElement("EmulatorType","TeknoS11"),new XElement("Controls",new XElement("Button","Space")),new XElement("ConfigValues",Field("Use Bezel","0",$"Fullscreen overlay: TeknoS11/bezels/{id}.png or default.png. Use transparent pixels."),Field("Windowed","1"),Field("Stretch to Fullscreen","1"))));
   var template=Path.Combine(tp,"GameProfiles",id+".xml");var profile=Path.Combine(tp,"UserProfiles",id+".xml");doc.Save(template);doc.Save(profile);return new(){Id=id,Name=id,TemplatePath=template,UserProfilePath=profile};
  }
  string PngAt(string relative,bool opaque=false) {var path=Path.Combine(imports,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,opaque?Png(true):png);return path;}
  string ZipAt(string relative) {var path=Path.Combine(imports,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);using var output=zip.CreateEntry("bezel.png").Open();output.Write(png);return path;}
  string Destination(GameRecord game)=>Path.Combine(tp,"TeknoS11","bezels",game.Id+".png");
  async Task<BezelPlanItem> Item(GameRecord game)=>(await service.PreviewAsync(settings,new[]{game})).Items.Single();
  var nested=Game("nested");var zipped=Game("zipped");var folder=Game("folder");var named=Game("named");var precedence=Game("precedence");
  var expected=new Dictionary<string,string> {
   [nested.Id]=PngAt(Path.Combine("BezelRepository-main","packs","system","NESTED.PNG")),
   [zipped.Id]=ZipAt(Path.Combine("BezelRepository-main","packs","system","zipped.zip")),
   [folder.Id]=PngAt(Path.Combine("BezelRepository-main","packs","folder","bezel.png")),
   [named.Id]=PngAt(Path.Combine("BezelRepository-main","packs","named","named.png"))
  };
  PngAt(Path.Combine("pack-one","precedence.png"));PngAt(Path.Combine("pack-two","precedence","bezel.png"));
  var mamePreferred=Path.Combine(mame,"precedence.png");File.WriteAllBytes(mamePreferred,png);expected[precedence.Id]=mamePreferred;
  var ready=new[]{nested,zipped,folder,named,precedence};var readyPlan=await service.PreviewAsync(settings,ready);
  foreach(var item in readyPlan.Items)Check(item.Action=="Install"&&item.Source==expected[item.ProfileId],"exact nested repository candidate and flat MAME precedence: "+item.ProfileId);
  settings.MameArtworkPath=mame+Path.DirectorySeparatorChar;
  try {var trailingMame=await Item(precedence);Check(trailingMame.Action=="Install"&&trailingMame.Source==mamePreferred,"a trailing separator on the MAME artwork root preserves source precedence");}
  finally {settings.MameArtworkPath=mame;}
  var rootFolder=Game("root-folder");var rootOverlay=PngAt(Path.Combine(rootFolder.Id,"bezel.png"));settings.BezelImportPath=Path.Combine(imports,rootFolder.Id)+Path.DirectorySeparatorChar;
  try {var trailingRepository=await Item(rootFolder);Check(trailingRepository.Action=="Install"&&trailingRepository.Source==rootOverlay,"a trailing separator on an identity-folder repository root still finds bezel.png");}
  finally {settings.BezelImportPath=imports;}

  var ambiguous=Game("ambiguous");PngAt(Path.Combine("pack-one","ambiguous.png"));ZipAt(Path.Combine("pack-two","ambiguous.zip"));
  var ambiguity=await Item(ambiguous);Check(ambiguity.Action=="NeedsReview"&&!ambiguity.Selected,"multiple recursive exact packages require review even when both are valid");
  var identityAmbiguity=Game("identity-ambiguous");PngAt(Path.Combine("pack-one","identity-ambiguous","bezel.png"));PngAt(Path.Combine("pack-two","identity-ambiguous","identity-ambiguous.png"));
  Check((await Item(identityAmbiguity)).Action=="NeedsReview","two exact identity folders must not be ranked by enumeration order");
  var opaque=Game("recursive-opaque");PngAt(Path.Combine("pack-one","recursive-opaque.png"),true);Check((await Item(opaque)).Action=="NeedsReview","recursive discovery still validates PNG transparency");
  var fuzzy=Game("fuzzy");PngAt(Path.Combine("packs","fuzzy (USA).png"));PngAt(Path.Combine("packs","fuzzy-extra","bezel.png"));PngAt(Path.Combine("packs","fuzzy-bezel.png"));PngAt(Path.Combine("packs","fuzzy.png.old"));PngAt(Path.Combine("packs","bezel.png"));
  Check((await Item(fuzzy)).Action=="Missing","unrecognized and fuzzy names are not guessed");
  var gitOnly=Game("git-only");PngAt(Path.Combine(".git","objects","git-only.png"));Check((await Item(gitOnly)).Action=="Missing","root .git data is excluded");
  var gitDuplicate=Game("git-duplicate");var realGitCandidate=PngAt(Path.Combine("export","git-duplicate.png"));PngAt(Path.Combine("export",".git","objects","git-duplicate.png"));
  Check((await Item(gitDuplicate)).Source==realGitCandidate,"nested .git data does not create a false duplicate");

  // Directory links must never let a repository scan leave its configured tree.
  var linked=Game("linked-only");var outside=Path.Combine(work,"outside-repository");Directory.CreateDirectory(outside);File.WriteAllBytes(Path.Combine(outside,"linked-only.png"),png);var linkPath=Path.Combine(imports,"linked-pack");bool linkCreated=false;
  try {Directory.CreateSymbolicLink(linkPath,outside);linkCreated=true;}
  catch(Exception ex)when(ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) {Console.WriteLine("Bezel reparse fixture unavailable on this host: "+ex.GetType().Name);}
  if(linkCreated) {
   try {Check((File.GetAttributes(linkPath)&FileAttributes.ReparsePoint)!=0,"link fixture has reparse-point attributes");Check((await Item(linked)).Action=="Missing","reparse directories are excluded from recursive sources");settings.BezelImportPath=linkPath;var linkedRoot=await Item(linked);Check(linkedRoot.Action!="Install"&&linkedRoot.Source.Length==0,"a configured repository root that is itself a link is not traversed");}
   finally {settings.BezelImportPath=imports;Directory.Delete(linkPath);}
  }
  var fileLinked=Game("file-linked");var linkedFile=Path.Combine(imports,"file-linked.png");bool fileLinkCreated=false;
  try {File.CreateSymbolicLink(linkedFile,Path.Combine(outside,"linked-only.png"));fileLinkCreated=true;}
  catch(Exception ex)when(ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) {Console.WriteLine("Bezel file-link fixture unavailable on this host: "+ex.GetType().Name);}
  if(fileLinkCreated) {try{Check((await Item(fileLinked)).Action=="Missing","reparse files are not accepted as local repository sources");}finally{File.Delete(linkedFile);}}

  var existing=Game("existing");Directory.CreateDirectory(Path.GetDirectoryName(Destination(existing))!);File.WriteAllBytes(Destination(existing),png);PngAt(Path.Combine("one","existing.png"));PngAt(Path.Combine("two","existing.png"));
  var existingBytes=File.ReadAllBytes(Destination(existing));var withExisting=ready.Append(existing).ToArray();var originals=withExisting.ToDictionary(g=>g.Id,g=>File.ReadAllBytes(g.UserProfilePath));var applyPlan=await service.PreviewAsync(settings,withExisting);
  Check(applyPlan.Items.Single(i=>i.ProfileId==existing.Id).Action=="Enable","an existing verified destination is preserved despite repository duplicates");
  var applied=await service.ApplyAsync(applyPlan);Check(applied.Succeeded==withExisting.Length&&applied.Errors.Count==0,"recursive overlays and existing overlay enable successfully apply");
  foreach(var game in withExisting) {Check(File.ReadAllBytes(Destination(game)).SequenceEqual(png),"validated recursive overlay contents preserved: "+game.Id);var profile=XDocument.Load(game.UserProfilePath);Check(profile.Root!.Element("Controls")!.Element("Button")!.Value=="Space","recursive install preserves controls: "+game.Id);Check(profile.Descendants("FieldInformation").Single(e=>e.Element("FieldName")!.Value=="Use Bezel").Element("FieldValue")!.Value=="1","recursive install enables bezel: "+game.Id);}
  Check(File.ReadAllBytes(Destination(existing)).SequenceEqual(existingBytes),"existing destination bytes never overwritten");Check(!(await service.PreviewAsync(settings,withExisting)).CanApply,"recursive repository installs remain idempotent");
  foreach(var journal in applyPlan.BackupPaths)await service.RollbackAsync(journal);
  foreach(var game in withExisting)Check(File.ReadAllBytes(game.UserProfilePath).SequenceEqual(originals[game.Id]),"recursive rollback restores exact profile bytes: "+game.Id);
  foreach(var game in ready)Check(!File.Exists(Destination(game)),"recursive rollback removes only the installed destination: "+game.Id);
  Check(File.ReadAllBytes(Destination(existing)).SequenceEqual(existingBytes),"rollback preserves preexisting bezel");

  // A previously unique source becoming ambiguous or lower priority invalidates the preview.
  async Task SourceSetChange(string id,Action addCandidate) {
   var game=Game(id);PngAt(Path.Combine("original",id+".png"));var profileBytes=File.ReadAllBytes(game.UserProfilePath);var preview=await service.PreviewAsync(settings,new[]{game});Check(preview.Items.Single().Action=="Install","source-set fixture starts with a unique valid overlay");
   var beforeEntries=Directory.GetFileSystemEntries(tp,"*",SearchOption.AllDirectories).OrderBy(p=>p).ToArray();addCandidate();
   var rejected=await service.ApplyAsync(preview);Check(rejected.Succeeded==0&&rejected.Errors.Any(e=>e.Contains("changed",StringComparison.OrdinalIgnoreCase)&&e.Contains("preview",StringComparison.OrdinalIgnoreCase)),"changed source candidate set is rejected: "+id);
   Check(!File.Exists(Destination(game))&&File.ReadAllBytes(game.UserProfilePath).SequenceEqual(profileBytes),"source-set rejection happens before profile/destination writes: "+id);Check(preview.BackupPaths.Count==0&&Directory.GetFileSystemEntries(tp,"*",SearchOption.AllDirectories).OrderBy(p=>p).SequenceEqual(beforeEntries),"source-set rejection creates no backup/staging files or directories: "+id);
  }
  await SourceSetChange("new-duplicate",()=>PngAt(Path.Combine("new-pack","new-duplicate.png")));
  await SourceSetChange("new-mame",()=>File.WriteAllBytes(Path.Combine(mame,"new-mame.png"),png));

  using(var cancelled=new CancellationTokenSource()) {cancelled.Cancel();bool previewCancelled=false;try{await service.PreviewAsync(settings,ready,ct:cancelled.Token);}catch(OperationCanceledException){previewCancelled=true;}Check(previewCancelled,"cancelled recursive preview propagates cancellation");
   var cancelledPlan=await service.PreviewAsync(settings,new[]{nested});bool applyCancelled=false;try{await service.ApplyAsync(cancelledPlan,ct:cancelled.Token);}catch(OperationCanceledException){applyCancelled=true;}Check(applyCancelled&&!File.Exists(Destination(nested))&&cancelledPlan.BackupPaths.Count==0,"cancelled apply leaves no installed overlay or journal");}
  using(var interrupted=new CancellationTokenSource()) {IEnumerable<GameRecord> CancelBetweenGames(){yield return nested;interrupted.Cancel();yield return zipped;}bool observed=false;try{await service.PreviewAsync(settings,CancelBetweenGames(),ct:interrupted.Token);}catch(OperationCanceledException){observed=true;}Check(observed,"preview cancellation between games is not converted into a review item");}
 }
 internal static byte[] Png(bool opaque) {
  using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});void Chunk(string type,byte[] data){Span<byte> number=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(number,(uint)data.Length);output.Write(number);var tag=Encoding.ASCII.GetBytes(type);output.Write(tag);output.Write(data);var crc=Crc32.Update(Crc32.Update(0xffffffff,tag),data);BinaryPrimitives.WriteUInt32BigEndian(number,~crc);output.Write(number);}
  var header=new byte[13];BinaryPrimitives.WriteUInt32BigEndian(header,32);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4),32);header[8]=8;header[9]=6;Chunk("IHDR",header);
  using var compressed=new MemoryStream();using(var zlib=new ZLibStream(compressed,CompressionLevel.SmallestSize,true)){for(int y=0;y<32;y++){zlib.WriteByte(0);for(int x=0;x<32;x++){zlib.Write(new byte[]{40,80,120,(byte)(opaque||x<3||x>=29||y<3||y>=29?255:0)});}}}Chunk("IDAT",compressed.ToArray());Chunk("IEND",Array.Empty<byte>());return output.ToArray();
 }
}

