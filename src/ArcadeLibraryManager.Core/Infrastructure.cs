
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ArcadeLibraryManager.Core;

public static class SettingsStore {
 public static readonly JsonSerializerOptions Json = new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
 public static AppSettings Load(string directory) { var path=Path.Combine(directory,"settings.json"); return File.Exists(path)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),Json)??new():new(); }
 public static void Save(string directory,AppSettings settings) {Directory.CreateDirectory(directory);SafeFiles.WriteText(Path.Combine(directory,"settings.json"),JsonSerializer.Serialize(settings,Json));}
}
public static class SafeFiles {
 public static string Hash(string path) {using var f=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(f));}
 public static string Child(string root,string relative) {
  relative=relative.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar);
  if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(':')||relative.Split(Path.DirectorySeparatorChar).Any(x=>x==".."))throw new InvalidDataException("Unsafe relative path: "+relative);
  foreach(var check in new[]{Path.GetFullPath(root),Path.GetFullPath(Path.Combine(root,relative))}) { try { if((File.GetAttributes(check)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked root or target is not allowed."); } catch(FileNotFoundException){} catch(DirectoryNotFoundException){} }
  var full=Path.GetFullPath(Path.Combine(root,relative));var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Path escapes destination.");
  var parent=Path.GetDirectoryName(full);
  while(parent!=null&&parent.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) {
   if(Directory.Exists(parent)&&(File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked destination directory is not allowed.");
   parent=Path.GetDirectoryName(parent);
  }
  return full;
 }
 public static void WriteText(string path,string text) {Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllText(temp,text,new UTF8Encoding(false));File.Move(temp,path,true);}
 public static void ExtractZip(string archive,string destination,CancellationToken ct,IProgress<JobEvent>? progress=null) {
  Directory.CreateDirectory(destination);using var zip=ZipFile.OpenRead(archive);
  long expanded=zip.Entries.Sum(x=>x.Length);var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
  if(drive.IsReady&&expanded>drive.AvailableFreeSpace)throw new IOException("Insufficient space for extracted archive.");
  int i=0;
  foreach(var e in zip.Entries) {
   ct.ThrowIfCancellationRequested();var target=Child(destination,e.FullName.TrimEnd('/','\\'));
   if(((e.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Archive symbolic links are not supported.");
   if(e.Name.Length==0){Directory.CreateDirectory(target);continue;}
   Directory.CreateDirectory(Path.GetDirectoryName(target)!);
   using(var input=e.Open())using(var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None)){
    var buffer=new byte[1024*1024];int n;uint crc=0xffffffff;
    while((n=input.Read(buffer,0,buffer.Length))!=0){ct.ThrowIfCancellationRequested();output.Write(buffer,0,n);crc=Crc32.Update(crc,buffer.AsSpan(0,n));}
    if(output.Length!=e.Length||~crc!=e.Crc32)throw new InvalidDataException("ZIP checksum mismatch: "+e.FullName);
   }
   if(++i%100==0)progress?.Report(new("Extracting",e.FullName,Percent:100d*i/zip.Entries.Count));
  }
 }
 public static IEnumerable<string> Files(string root,CancellationToken ct,Action<string>? error=null) {
  var queue=new Stack<string>();if(Directory.Exists(root))queue.Push(Path.GetFullPath(root));
  while(queue.Count>0){ct.ThrowIfCancellationRequested();var dir=queue.Pop();string[] files,dirs;
   try{files=Directory.GetFiles(dir);dirs=Directory.GetDirectories(dir);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){error?.Invoke(dir+": "+e.Message);continue;}
   foreach(var f in files){ct.ThrowIfCancellationRequested();if((File.GetAttributes(f)&FileAttributes.ReparsePoint)==0)yield return f;}
   foreach(var d in dirs){try{if((File.GetAttributes(d)&FileAttributes.ReparsePoint)==0&&!Path.GetFileName(d).StartsWith(".alm-"))queue.Push(d);}catch(IOException){}}
  }
 }
}
public static class Crc32 {
 static readonly uint[] Table=Enumerable.Range(0,256).Select(i=>{uint c=(uint)i;for(int j=0;j<8;j++)c=(c&1)!=0?0xedb88320^(c>>1):c>>1;return c;}).ToArray();
 public static uint Update(uint crc,ReadOnlySpan<byte> bytes){foreach(byte b in bytes)crc=Table[(crc^b)&255]^(crc>>8);return crc;}
 public static string FileHash(string path){using var f=File.OpenRead(path);var buffer=new byte[1024*1024];int n;uint c=0xffffffff;while((n=f.Read(buffer))>0)c=Update(c,buffer.AsSpan(0,n));return (~c).ToString("x8");}
}
public sealed class JobStore {
 readonly string path;
 public JobStore(string dataDir){Directory.CreateDirectory(dataDir);path=Path.Combine(dataDir,"library.db");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS Jobs(Id TEXT PRIMARY KEY,Name TEXT,Stage TEXT,Message TEXT,UpdatedUtc TEXT,Plan TEXT); CREATE TABLE IF NOT EXISTS Files(Path TEXT PRIMARY KEY,Name TEXT,Size INTEGER,Modified INTEGER,Root TEXT); CREATE INDEX IF NOT EXISTS IX_Files_Name ON Files(Name);";cmd.ExecuteNonQuery();}
 SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,DefaultTimeout=15}.ToString());c.Open();return c;}
 public void Set(string id,string name,string stage,string message,InstallPlanItem? plan=null){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO Jobs VALUES($i,$n,$s,$m,$t,$p) ON CONFLICT(Id) DO UPDATE SET Name=$n,Stage=$s,Message=$m,UpdatedUtc=$t,Plan=COALESCE($p,Jobs.Plan)";cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$n",name);cmd.Parameters.AddWithValue("$s",stage);cmd.Parameters.AddWithValue("$m",message);cmd.Parameters.AddWithValue("$t",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$p",plan==null?DBNull.Value:JsonSerializer.Serialize(plan));cmd.ExecuteNonQuery();}
 public List<JobRecord> List(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Id,Name,Stage,Message,UpdatedUtc FROM Jobs ORDER BY UpdatedUtc DESC";using var r=cmd.ExecuteReader();var rows=new List<JobRecord>();while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));return rows;}
 public List<InstallPlanItem> Pending(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Plan FROM Jobs WHERE Stage NOT IN ('Complete','Skipped') AND Plan IS NOT NULL";using var r=cmd.ExecuteReader();var rows=new List<InstallPlanItem>();while(r.Read()){var item=JsonSerializer.Deserialize<InstallPlanItem>(r.GetString(0));if(item!=null)rows.Add(item);}return rows;}
 public void Index(string root,IEnumerable<string> files,CancellationToken ct,IProgress<JobEvent>? progress){
  using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO Files VALUES($p,$n,$s,$m,$r) ON CONFLICT(Path) DO UPDATE SET Name=$n,Size=$s,Modified=$m,Root=$r";
  foreach(var key in new[]{"$p","$n","$s","$m","$r"})cmd.Parameters.Add(new SqliteParameter(key,""));int count=0;
  foreach(var f in files){ct.ThrowIfCancellationRequested();try{var info=new FileInfo(f);cmd.Parameters["$p"].Value=f;cmd.Parameters["$n"].Value=info.Name.ToLowerInvariant();cmd.Parameters["$s"].Value=info.Length;cmd.Parameters["$m"].Value=info.LastWriteTimeUtc.Ticks;cmd.Parameters["$r"].Value=root;cmd.ExecuteNonQuery();if(++count%1000==0)progress?.Report(new("Indexing",$"{count:N0} relevant files indexed in {root}"));}catch(IOException){}}
  tx.Commit();
 }
 public List<string> Find(string name){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Path FROM Files WHERE Name=$n";cmd.Parameters.AddWithValue("$n",name.ToLowerInvariant());using var r=cmd.ExecuteReader();var list=new List<string>();while(r.Read()){var path=r.GetString(0);if(File.Exists(path))list.Add(path);}return list;}
}
public sealed class DownloadService {
 static readonly HttpClient DefaultClient=new(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.None}){Timeout=Timeout.InfiniteTimeSpan};
 readonly int mbps;readonly IProgress<JobEvent>? progress; readonly HttpClient Client;
 public DownloadService(int maxMbps=0,IProgress<JobEvent>? progress=null,HttpClient? client=null){mbps=maxMbps;this.progress=progress;Client=client??DefaultClient;}
 public async Task GetAsync(string url,string path,long expectedSize,CancellationToken ct) {
  if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")throw new InvalidDataException("Downloads require an HTTPS URL.");
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);var partial=path+".part";var statePath=partial+".json";
  string source="";string? etag=null;
  if(File.Exists(statePath)){try{using var state=JsonDocument.Parse(File.ReadAllText(statePath));source=state.RootElement.GetProperty("Url").GetString()??"";etag=state.RootElement.GetProperty("ETag").GetString();}catch(JsonException){}}
  if(File.Exists(partial)&&source!=url)throw new IOException("Partial download belongs to another source; use another cache directory or review the partial.");
  for(int attempt=0;attempt<4;attempt++){
   ct.ThrowIfCancellationRequested();long offset=File.Exists(partial)?new FileInfo(partial).Length:0;
   if(expectedSize>0&&offset==expectedSize){File.Move(partial,path,true);return;}
   try{
    using var req=new HttpRequestMessage(HttpMethod.Get,uri);req.Headers.UserAgent.ParseAdd("ArcadeLibraryManager/0.2");
    if(offset>0){req.Headers.Range=new RangeHeaderValue(offset,null);if(etag!=null)req.Headers.TryAddWithoutValidation("If-Range",etag);}
    using var response=await Client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
    if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable)throw new InvalidDataException("Server rejected saved range; partial retained for review.");
    response.EnsureSuccessStatusCode();
    bool append=offset>0&&response.StatusCode==HttpStatusCode.PartialContent;
    if(append&&response.Content.Headers.ContentRange?.From!=offset)throw new InvalidDataException("Server returned an unexpected byte range.");
    if(!append)offset=0;
    etag=response.Headers.ETag?.ToString();
    SafeFiles.WriteText(statePath,JsonSerializer.Serialize(new{Url=url,ETag=etag}));
    await using var input=await response.Content.ReadAsStreamAsync(ct);await using var output=new FileStream(partial,append?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read,1024*1024,true);
    var buffer=new byte[1024*1024];var watch=System.Diagnostics.Stopwatch.StartNew();long newBytes=0,lastReport=0;int n;
    while((n=await input.ReadAsync(buffer,ct))>0){await output.WriteAsync(buffer.AsMemory(0,n),ct);newBytes+=n;if(mbps>0){var expectedMs=(offset+newBytes-offset)*8000d/(mbps*1_000_000d);var wait=expectedMs-watch.Elapsed.TotalMilliseconds;if(wait>0)await Task.Delay((int)Math.Min(wait,2000),ct);}
     if(watch.ElapsedMilliseconds-lastReport>500){progress?.Report(new("Downloading",$"{Path.GetFileName(path)}: {(offset+newBytes)/1048576d:N1} MiB",Percent:expectedSize>0?100d*(offset+newBytes)/expectedSize:null));lastReport=watch.ElapsedMilliseconds;}
    }
    await output.FlushAsync(ct);output.Close();
    if(expectedSize>0&&new FileInfo(partial).Length!=expectedSize)throw new IOException("Downloaded archive has unexpected size.");
    File.Move(partial,path,true);return;
   }catch(Exception e)when((e is HttpRequestException||e is IOException)&&attempt<3){progress?.Report(new("Retry",$"Transfer retry {attempt+1}/3: {e.Message}"));await Task.Delay(TimeSpan.FromSeconds(2*(attempt+1)),ct);}
  }
 }
 public static async Task VerifyAsync(string path,long size,string md5,string sha1,CancellationToken ct){
  if(size>0&&new FileInfo(path).Length!=size)throw new InvalidDataException("Archive size mismatch.");
  if(string.IsNullOrWhiteSpace(md5)&&string.IsNullOrWhiteSpace(sha1))throw new InvalidDataException("No trusted archive checksum is available.");
  await using var f=File.OpenRead(path);var actual=!string.IsNullOrWhiteSpace(sha1)?Convert.ToHexString(await SHA1.HashDataAsync(f,ct)):Convert.ToHexString(await MD5.HashDataAsync(f,ct));
  if(!actual.Equals(!string.IsNullOrWhiteSpace(sha1)?sha1:md5,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Archive checksum mismatch; file retained for review.");
 }
}

