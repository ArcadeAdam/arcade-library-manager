
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed class ArchiveSource {
 readonly AppSettings settings; readonly string data; readonly IProgress<JobEvent>? progress;
 List<ArchiveFile>? files;
 public sealed record ArchiveFile(string Item,string Name,long Size,string Md5,string Sha1) {
  public string Url=>"https://archive.org/download/"+Uri.EscapeDataString(Item)+"/"+string.Join("/",Name.Split('/').Select(Uri.EscapeDataString));
 }
 public ArchiveSource(AppSettings s,string data,IProgress<JobEvent>? progress){settings=s;this.data=data;this.progress=progress;}
 public async Task<ArchiveFile?> FindAsync(GameRecipe recipe,CancellationToken ct) {
  files??=await LoadAsync(ct);
  return files.FirstOrDefault(f=>Path.GetFileName(f.Name).Equals(Path.GetFileName(recipe.ArchiveName),StringComparison.OrdinalIgnoreCase)
   &&(string.IsNullOrEmpty(recipe.ArchiveSha1)||f.Sha1.Equals(recipe.ArchiveSha1,StringComparison.OrdinalIgnoreCase)));
 }
 async Task<List<ArchiveFile>> LoadAsync(CancellationToken ct){
  if(!Uri.TryCreate(settings.ArchiveUrl,UriKind.Absolute,out var uri)||uri.Host!="archive.org"||uri.Scheme!="https")throw new InvalidDataException("This build supports HTTPS Archive.org item or collection URLs.");
  var parts=uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);
  if(parts.Length<2||parts[0] is not ("details" or "download"))throw new InvalidDataException("Enter an Archive.org /details/item URL.");
  var cache=Path.Combine(data,"archive-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.ArchiveUrl)))[..16]+".json");
  if(File.Exists(cache)&&File.GetLastWriteTimeUtc(cache)>DateTime.UtcNow.AddDays(-1))return JsonSerializer.Deserialize<List<ArchiveFile>>(File.ReadAllText(cache))??new();
  using var client=new HttpClient(){Timeout=TimeSpan.FromSeconds(60)};client.DefaultRequestHeaders.UserAgent.ParseAdd("ArcadeLibraryManager/0.2");
  var queue=new Queue<string>();queue.Enqueue(Uri.UnescapeDataString(parts[1]));var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var result=new List<ArchiveFile>();
  while(queue.Count>0&&seen.Count<32){
   ct.ThrowIfCancellationRequested();var id=queue.Dequeue();if(!seen.Add(id))continue;
   progress?.Report(new("Archive catalog","Reading "+id));
   using var doc=JsonDocument.Parse(await client.GetStringAsync("https://archive.org/metadata/"+Uri.EscapeDataString(id),ct));
   if(doc.RootElement.TryGetProperty("files",out var list))foreach(var f in list.EnumerateArray()){
    var name=f.GetProperty("name").GetString()??"";if(!name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))continue;
    long.TryParse(f.TryGetProperty("size",out var size)?size.ToString():"0",out var bytes);
    result.Add(new(id,name,bytes,f.TryGetProperty("md5",out var md5)?md5.GetString()??"":"",f.TryGetProperty("sha1",out var sha1)?sha1.GetString()??"":""));
   }
   if(doc.RootElement.TryGetProperty("metadata",out var meta)&&meta.TryGetProperty("description",out var description)){
    foreach(Match m in Regex.Matches(description.ToString(),@"archive\.org/(?:details|download)/([A-Za-z0-9_.-]+)"))if(!seen.Contains(m.Groups[1].Value))queue.Enqueue(m.Groups[1].Value);
   }
  }
  if(result.Count==0)throw new InvalidDataException("No ZIP game archives found in this source or its linked items.");
  SafeFiles.WriteText(cache,JsonSerializer.Serialize(result));return result;
 }
}
public sealed class HttpRangeStream : Stream {
 readonly HttpClient client=new(){Timeout=TimeSpan.FromSeconds(60)};readonly string url;readonly long length;long position;readonly CancellationToken ct;
 readonly Dictionary<long,byte[]> cache=new();const int Block=256*1024;
 public HttpRangeStream(string url,long length,CancellationToken ct){this.url=url;this.length=length;this.ct=ct;}
 public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;
 public override long Position{get=>position;set{if(value<0)throw new IOException("Negative offset");position=value;}}
 public override int Read(byte[] buffer,int offset,int count){
  if(position>=length)return 0;int total=0;
  while(count>0&&position<length){
   ct.ThrowIfCancellationRequested();long start=position/Block*Block;
   if(!cache.TryGetValue(start,out var bytes)){
    using var req=new HttpRequestMessage(HttpMethod.Get,url);req.Headers.Range=new RangeHeaderValue(start,Math.Min(length-1,start+Block-1));
    using var response=client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct).GetAwaiter().GetResult();
    if(response.StatusCode!=HttpStatusCode.PartialContent||response.Content.Headers.ContentRange?.From!=start)throw new IOException("Source does not support exact archive range reads.");
    long expectedEnd=Math.Min(length-1,start+Block-1);int expectedLength=checked((int)(expectedEnd-start+1));
    if(response.Content.Headers.ContentRange?.To!=expectedEnd||response.Content.Headers.ContentRange?.Length!=length)throw new IOException("Archive range metadata mismatch.");
    if(response.Content.Headers.ContentLength is long declared && declared!=expectedLength)throw new IOException("Archive range length mismatch.");
    bytes=new byte[expectedLength];using(var body=response.Content.ReadAsStream(ct)){int have=0;while(have<bytes.Length){ct.ThrowIfCancellationRequested();int received=body.Read(bytes,have,bytes.Length-have);if(received==0)throw new EndOfStreamException();have+=received;}if(body.ReadByte()!=-1)throw new IOException("Archive range exceeds requested length.");}
    if(cache.Count>24)cache.Clear();cache[start]=bytes;
   }
   int at=(int)(position-start),n=Math.Min(count,bytes.Length-at);if(n<=0)throw new EndOfStreamException();
   Array.Copy(bytes,at,buffer,offset,n);position+=n;offset+=n;count-=n;total+=n;
  }return total;
 }
 public override long Seek(long offset,SeekOrigin origin){Position=(origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:length)+offset;return position;}
 public override void Flush(){} public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
 protected override void Dispose(bool disposing){if(disposing)client.Dispose();base.Dispose(disposing);}
}
public sealed record ValidationResult(bool Success,string Message,List<string> ZipPaths,string DiskPath="");
public static class GameValidation {
 public static ValidationResult RomSet(GameRecipe recipe,Func<string,List<string>> find,bool readContents,CancellationToken ct){
  var candidates=recipe.DependencySets.Append(recipe.RomSet).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(s=>find(s+".zip")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  var found=new HashSet<string>();var used=new List<string>();
  foreach(var path in candidates){
   ct.ThrowIfCancellationRequested();
   try{using var zip=ZipFile.OpenRead(path);bool uses=false;
    foreach(var rom in recipe.Roms){
     var e=zip.Entries.FirstOrDefault(e=>e.Length==rom.Size&&e.Crc32.ToString("x8").Equals(rom.Crc,StringComparison.OrdinalIgnoreCase));
     if(e==null)continue;
     if(readContents){using var input=e.Open();var hash=Convert.ToHexString(SHA1.HashData(input));if(!string.IsNullOrEmpty(rom.Sha1)&&!hash.Equals(rom.Sha1,StringComparison.OrdinalIgnoreCase))continue;}
     found.Add(rom.Crc+":"+rom.Size);uses=true;
    }if(uses)used.Add(path);
   }catch(InvalidDataException){}
  }
  var missing=recipe.Roms.Where(r=>!found.Contains(r.Crc+":"+r.Size)).Select(r=>r.Name).ToList();
  if(missing.Count>0){
   var requiredZips=recipe.DependencySets.Append(recipe.RomSet).Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).Select(s=>s+".zip");
   return new(false,"Missing or incorrect ROM: "+string.Join(", ",missing.Take(6))+". Required game/parent/device ZIPs: "+string.Join(", ",requiredZips)+". Add the matching game/parent/device ZIPs to a configured ROM source, index it, and build a fresh plan. Completed downloads are retained.",used);
  }
  string diskPath="";
  foreach(var disk in recipe.Disks){
   var valid=find(disk.Name).Where(f=>ReadChdSha1(f).Equals(disk.Sha1,StringComparison.OrdinalIgnoreCase)).ToList();
   if(valid.Count==0)return new(false,"Missing or incorrect CHD: "+disk.Name,used);
   if(diskPath=="")diskPath=valid[0];
  }
  return new(true,"ROM CRC/size and CHD identity verified",used,diskPath);
 }
 public static string ReadChdSha1(string path){
  try{using var f=File.OpenRead(path);var b=new byte[124];if(f.Read(b,0,b.Length)<108||System.Text.Encoding.ASCII.GetString(b,0,8)!="MComprHD")return "";
   uint version=BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12,4));return version==5?Convert.ToHexString(b.AsSpan(84,20)):version==4?Convert.ToHexString(b.AsSpan(48,20)):"";
  }catch(IOException){return "";}
 }
 public static void Acgame(string primary){
  if(!primary.EndsWith(".acgame",StringComparison.OrdinalIgnoreCase))return;
  var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var line in File.ReadLines(primary)){var s=line.Trim();if(s.StartsWith(';')||s.StartsWith('#')||!s.Contains('='))continue;var a=s.Split('=',2);values[a[0].Trim()]=a[1].Trim().Trim('"');}
  var root=Path.GetDirectoryName(primary)!;if(values.TryGetValue("subdir",out var sub)&&sub!="")root=SafeFiles.Child(root,sub);
  foreach(var key in new[]{"elf","dongle","mediasrc"}){if(!values.TryGetValue(key,out var relative)||string.IsNullOrEmpty(relative)||!File.Exists(SafeFiles.Child(root,relative)))throw new InvalidDataException("Missing "+key+" dependency for "+Path.GetFileName(primary));}
 }
}


