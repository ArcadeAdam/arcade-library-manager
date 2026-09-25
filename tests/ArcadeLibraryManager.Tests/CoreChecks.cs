
using ArcadeLibraryManager.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public static class CoreChecks {
 static void Assert(bool test,string message){if(!test)throw new Exception(message);}
 static void Zip(string path,params(string Name,string Body)[] files){Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var z=ZipFile.Open(path,ZipArchiveMode.Create);foreach(var f in files){using var w=new StreamWriter(z.CreateEntry(f.Name).Open());w.Write(f.Body);}}
 static void Profile(string tp,string id,bool two=false){Directory.CreateDirectory(Path.Combine(tp,"GameProfiles"));File.WriteAllText(Path.Combine(tp,"GameProfiles",id+".xml"),$"<GameProfile><GamePath></GamePath><GamePath2></GamePath2><EmulatorType>Test</EmulatorType><ExecutableName>game.exe</ExecutableName><HasTwoExecutables>{two.ToString().ToLower()}</HasTwoExecutables><JoystickButtons><ButtonName>OWNER CONTROL</ButtonName></JoystickButtons><Custom>Keep me</Custom></GameProfile>");}
 static GameRecipe Recipe(string id,string archive,string primary){using var f=File.OpenRead(archive);return new(){Id=id,Name=id,PayloadId=id,ArchiveName=Path.GetFileName(archive),ArchiveSize=f.Length,ArchiveSha1=Convert.ToHexString(SHA1.HashData(f)),PrimaryPath=primary};}
 static void SaveRecipes(string data,params GameRecipe[] recipes){Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"recipes.json"),JsonSerializer.Serialize(recipes));}
 public static async Task<List<string>> RunAsync(string root){
  Directory.CreateDirectory(root);var pass=new List<string>();
  var tp=Path.Combine(root,"tp");var roms=Path.Combine(root,"roms");var destination=Path.Combine(root,"installed");var data=Path.Combine(root,"data");Directory.CreateDirectory(roms);
  var settings=new AppSettings{TeknoParrotPath=tp,DestinationPath=destination,RomRoots=new(){roms}};
  Profile(tp,"alpha");var archive=Path.Combine(roms,"alpha-package.zip");Zip(archive,("bin/game.exe","fixture-executable"),("data/payload.txt","game data"));
  var recipe=Recipe("alpha",archive,"bin/game.exe");SaveRecipes(data,recipe);
  var engine=new AppEngine(settings,data,volumeHealth:VolumeHealthChecks.CleanProvider());await engine.IndexRomsAsync(default);
  var plan=await engine.PlanAsync(new[]{"alpha"},default);
  Assert(plan.Single().Action=="Extract local archive","Local archive not selected");
  var result=await engine.ExecuteAsync(plan,default);Assert(result.Succeeded==1&&result.Errors.Count==0,string.Join("\n",result.Errors));
  var profile=Path.Combine(tp,"UserProfiles","alpha.xml");var doc=XDocument.Load(profile);
  Assert(doc.Root!.Element("Custom")!.Value=="Keep me"&&doc.Root.Element("JoystickButtons")!.Value=="OWNER CONTROL","Controls or custom fields altered");
  Assert(File.Exists(doc.Root.Element("GamePath")!.Value),"Registered primary missing");pass.Add("Verified extraction and profile registration preserve controls");
  var second=await engine.PlanAsync(new[]{"alpha"},default);Assert(second.Single().Action=="Already installed"&&!second.Single().Selected,"Repeated import was not idempotent");pass.Add("Repeated installation produces no duplicates");
  doc.Root.Element("GamePath")!.Value=Path.Combine(root,"missing.exe");doc.Save(profile);await engine.IndexRomsAsync(default);
  var repair=await engine.PlanAsync(new[]{"alpha"},default);Assert(repair.Single().Action=="Use local files","Exact local payload did not match archive");
  doc.Root.Element("Custom")!.Value="Changed after preview";doc.Save(profile);
  result=await engine.ExecuteAsync(repair,default);Assert(result.Errors.Count==1&&XDocument.Load(profile).Root!.Element("Custom")!.Value=="Changed after preview","Concurrent profile edit overwritten");pass.Add("Concurrent profile edit blocks outdated repair plan");
  Profile(tp,"broken");var ac=Path.Combine(roms,"broken.zip");Zip(ac,("game.acgame","[data]\nsubdir=data\nelf=boot.elf\ndongle=key.ps2\nmediasrc=media.chd"),("data/boot.elf","elf"),("data/media.chd","disc"));
  SaveRecipes(data,recipe,Recipe("broken",ac,"game.acgame"));engine=new(settings,data,volumeHealth:VolumeHealthChecks.CleanProvider());await engine.IndexRomsAsync(default);
  var broken=await engine.PlanAsync(new[]{"broken"},default);result=await engine.ExecuteAsync(broken,default);
  Assert(result.Errors.Count==1&&!File.Exists(Path.Combine(tp,"UserProfiles","broken.xml"))&&!Directory.Exists(Path.Combine(destination,"broken")),"Incomplete manifest marked installed");
  Assert(engine.GetPendingPlan().Any(p=>p.ProfileId=="broken"),"Failed job not durable");pass.Add("Missing dongle prevents commit and survives in retry journal");
  var romzip=Path.Combine(roms,"classic.zip");Zip(romzip,("real.rom","ROM content"));
  using(var z=ZipFile.OpenRead(romzip)){
   var e=z.Entries[0];var rr=new GameRecipe{Id="classic",PayloadId="classic",RomSet="classic",PrimaryPath="classic.zip",DependencySets=new(){"classic"},Roms=new(){new(){Name="real.rom",Size=e.Length,Crc=e.Crc32.ToString("x8"),Sha1=Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("ROM content")))} }};
   var ok=GameValidation.RomSet(rr,n=>n=="classic.zip"?new(){romzip}:new(),true,default);Assert(ok.Success,"Valid ROM was rejected");
   rr.Roms.Add(new(){Name="missing.flash",Size=524288,Crc="f87f18cf"});Assert(!GameValidation.RomSet(rr,n=>n=="classic.zip"?new(){romzip}:new(),true,default).Success,"Missing flash accepted");pass.Add("ROM contents validated and missing flash rejected");
  }
  var malicious=Path.Combine(root,"malicious.zip");Zip(malicious,("../escaped.txt","no"));
  try{SafeFiles.ExtractZip(malicious,Path.Combine(root,"safe"),default);throw new Exception("Traversal accepted");}catch(InvalidDataException){}
  Assert(!File.Exists(Path.Combine(root,"escaped.txt")),"Archive escaped destination");pass.Add("Archive traversal blocked");
  foreach(var relative in new[]{"../x","C:\\x","a:b","/rooted"}){try{SafeFiles.Child(root,relative);throw new Exception("Unsafe path accepted");}catch(InvalidDataException){}}
  pass.Add("Absolute paths and alternate data streams blocked");
  var bytes=Encoding.UTF8.GetBytes("abcdefghij");var partial=Path.Combine(root,"range.zip");File.WriteAllBytes(partial+".part",bytes[..4]);File.WriteAllText(partial+".part.json",JsonSerializer.Serialize(new{Url="https://fixture.test/file",ETag="\"v1\""}));
  using(var client=new HttpClient(new FakeHandler(request=>{
   Assert(request.Headers.Range?.Ranges.Single().From==4,"Resume offset wrong");var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(bytes[4..])};
   response.Content.Headers.ContentRange=new ContentRangeHeaderValue(4,9,10);return response;
  })))await new DownloadService(client:client).GetAsync("https://fixture.test/file",partial,10,default);
  Assert(File.ReadAllBytes(partial).SequenceEqual(bytes),"Resume bytes incorrect");await DownloadService.VerifyAsync(partial,10,"",Convert.ToHexString(SHA1.HashData(bytes)),default);pass.Add("HTTP range resume yields exact verified file");
  var ignored=Path.Combine(root,"range-ignored.zip");File.WriteAllBytes(ignored+".part",bytes[..3]);File.WriteAllText(ignored+".part.json",JsonSerializer.Serialize(new{Url="https://fixture.test/file",ETag="\"v1\""}));
  using(var client=new HttpClient(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})))await new DownloadService(client:client).GetAsync("https://fixture.test/file",ignored,10,default);
  Assert(File.ReadAllBytes(ignored).SequenceEqual(bytes),"Server ignored range but file appended");pass.Add("Servers ignoring ranges restart without corrupt append");
  var cancelled=new CancellationToken(true);
  try{await engine.ExecuteAsync(broken,cancelled);throw new Exception("Cancelled job ran");}catch(OperationCanceledException){}
  pass.Add("Cancellation leaves no completed profile");
  SettingsStore.Save(data,settings);Assert(SettingsStore.Load(data).RomRoots.Single()==roms,"Settings failed roundtrip");pass.Add("Portable settings persist without secrets");
  var dirtyDestination=Path.Combine(root,"dirty-output-must-not-exist");
  var dirtySettings=new AppSettings{TeknoParrotPath=tp,DestinationPath=dirtyDestination};
  var dirtyProvider=new VolumeHealthService((p,token)=>Task.FromResult(new VolumeHealthResult(p,Path.GetPathRoot(p)??"fixture","NTFS",VolumeHealthState.Dirty,"Fixture")));
  var guardedEngine=new AppEngine(dirtySettings,data,volumeHealth:dirtyProvider);
  try{await guardedEngine.ExecuteAsync(broken,default);throw new Exception("Dirty volume was allowed");}catch(VolumeHealthException){}
  Assert(!Directory.Exists(dirtyDestination),"Dirty destination created before readiness check");pass.Add("Dirty volumes block game installation before destination writes");
  return pass;
 }
 sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> response):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(response(request));}
}

