using ArcadeLibraryManager.Core;
public static class VolumeHealthChecks {
 static void Check(bool value,string reason){if(!value)throw new Exception("Volume health check failed: "+reason);}
 public static VolumeHealthService CleanProvider()=>new((path,ct)=>Task.FromResult(new VolumeHealthResult(path,Path.GetPathRoot(path)??"fixture","NTFS",VolumeHealthState.Clean,"Fixture only; no native calls.")));
 public static async Task RunAsync(string root) {
  var calls=new List<string>();var service=new VolumeHealthService((path,ct)=>{calls.Add(path);return Task.FromResult(new VolumeHealthResult(path,Path.GetPathRoot(path)??"fixture","NTFS",path.Contains("dirty",StringComparison.OrdinalIgnoreCase)?VolumeHealthState.Dirty:path.Contains("unknown",StringComparison.OrdinalIgnoreCase)?VolumeHealthState.Unknown:VolumeHealthState.Clean,"Injected read-only result."));});
  var dirty=Path.Combine(root,"dirty","must-not-be-created");bool mutated=false;try{await service.EnsureWritableAsync(new[]{dirty});Directory.CreateDirectory(dirty);mutated=true;}catch(VolumeHealthException ex){Check(ex.Results.Single().State==VolumeHealthState.Dirty,"dirty state retained");Check(ex.Message.Contains("No automatic repair"),"no automatic repair promised");}Check(!mutated&&!Directory.Exists(dirty),"dirty gate precedes writes");
  var unknown=Path.Combine(root,"unknown");try{await service.EnsureWritableAsync(new[]{unknown});throw new Exception("Unknown state was incorrectly accepted.");}catch(VolumeHealthException ex){Check(ex.Results.Single().State==VolumeHealthState.Unknown,"permission failures stay unknown");}
  var clean=Path.Combine(root,"clean");var result=await service.EnsureWritableAsync(new[]{clean});Check(result.Single().State==VolumeHealthState.Clean,"clean NTFS accepted");Check(!Directory.Exists(clean),"health service itself made no directory");
  var nonNtfs=new VolumeHealthService((path,ct)=>Task.FromResult(new VolumeHealthResult(path,"fixture","exFAT",VolumeHealthState.NotApplicable,"Not an NTFS filesystem.")));Check((await nonNtfs.EnsureWritableAsync(new[]{clean})).Single().State==VolumeHealthState.NotApplicable,"nonNTFS not represented as healthy");
  var denied=new VolumeHealthService((path,ct)=>throw new UnauthorizedAccessException("Fixture permission failure"));Check((await denied.GetVolumeHealthAsync(clean)).State==VolumeHealthState.Unknown,"query exception becomes unknown");
  using var cts=new CancellationTokenSource();cts.Cancel();try{await service.GetVolumeHealthAsync(clean,cts.Token);throw new Exception("Canceled check completed.");}catch(OperationCanceledException){}
  Check(calls.Count>=3,"injected provider exercised");Console.WriteLine("Volume health checks passed: dirty/unknown blocked before writes, clean/nonNTFS distinguished, no repairs or native mutation, permission failures and cancellation.");
 }
}
