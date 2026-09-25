using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ArcadeLibraryManager.Core;

public enum VolumeHealthState { Clean, Dirty, Unknown, NotApplicable, ReadOnly }
public sealed record VolumeHealthResult(string Path,string Volume,string FileSystem,VolumeHealthState State,string Detail) {
 public bool BlocksWrites => State is VolumeHealthState.Dirty or VolumeHealthState.Unknown or VolumeHealthState.ReadOnly;
 public string Explanation => VolumeHealthService.Explain(this);
}
public sealed class VolumeHealthException : IOException {
 public IReadOnlyList<VolumeHealthResult> Results { get; }
 public VolumeHealthException(IReadOnlyList<VolumeHealthResult> results):base(string.Join(Environment.NewLine,results.Where(r=>r.BlocksWrites).Select(VolumeHealthService.Explain)))=>Results=results;
}

/// <summary>
/// Read-only Windows filesystem readiness check. This service never invokes repairs, requests a write handle,
/// dismounts a volume, changes a dirty flag, or starts a surface/SMART test. A clear dirty flag is not a claim
/// of physical-disk health. Unknown query results are deliberately not treated as clean.
/// </summary>
public sealed class VolumeHealthService {
 private readonly Func<string,CancellationToken,Task<VolumeHealthResult>> provider;
 public VolumeHealthService(Func<string,CancellationToken,Task<VolumeHealthResult>>? healthProvider=null) => provider=healthProvider??((path,ct)=>Task.Run(()=>QueryWindows(path,ct),ct));
 public async Task<VolumeHealthResult> GetVolumeHealthAsync(string path,CancellationToken ct=default) {
  ct.ThrowIfCancellationRequested();
  if(string.IsNullOrWhiteSpace(path))return new(path??"","","",VolumeHealthState.Unknown,"No destination path was supplied.");
  try {var full=System.IO.Path.GetFullPath(path);var result=await provider(full,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();return result;}
  catch(OperationCanceledException){throw;}
  catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception) {return new(path,"","",VolumeHealthState.Unknown,"The volume could not be inspected: "+ex.Message);}
 }
 public async Task<IReadOnlyList<VolumeHealthResult>> GetVolumeHealthAsync(IEnumerable<string> paths,CancellationToken ct=default) {
  ArgumentNullException.ThrowIfNull(paths);var results=new List<VolumeHealthResult>();var checkedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  // Resolve each distinct full path, not just its drive letter: junctions and mounted folders can cross volumes.
  foreach(var path in paths){ct.ThrowIfCancellationRequested();if(checkedPaths.Add(path??""))results.Add(await GetVolumeHealthAsync(path??"",ct).ConfigureAwait(false));}
  return results;
 }
 public async Task<IReadOnlyList<VolumeHealthResult>> EnsureWritableAsync(IEnumerable<string> paths,CancellationToken ct=default) {
  var results=await GetVolumeHealthAsync(paths,ct).ConfigureAwait(false);if(results.Count==0)throw new VolumeHealthException(new[]{new VolumeHealthResult("","","",VolumeHealthState.Unknown,"No write destinations were supplied.")});
  if(results.Any(r=>r.BlocksWrites))throw new VolumeHealthException(results);return results;
 }
 public IReadOnlyList<VolumeHealthResult> EnsureWritable(IEnumerable<string> paths,CancellationToken ct=default)=>EnsureWritableAsync(paths,ct).GetAwaiter().GetResult();
 public static string Explain(VolumeHealthResult result) {
  var label=string.IsNullOrWhiteSpace(result.Volume)?result.Path:result.Volume;
  return result.State switch {
   VolumeHealthState.Dirty=>$"Writes paused for {label}: Windows marks this NTFS volume dirty or needing filesystem repair. {result.Detail} No automatic repair was attempted.",
   VolumeHealthState.ReadOnly=>$"Writes paused for {label}: the volume is read-only. {result.Detail}",
   VolumeHealthState.Unknown=>$"Writes paused for {label}: filesystem readiness is unknown. {result.Detail} The app has not assumed this volume is healthy or attempted a repair.",
   VolumeHealthState.Clean=>$"{label}: the NTFS dirty flag is clear. This check does not assess physical-disk health.",
   _=>$"{label}: {result.Detail} No NTFS health result is claimed."
  };
 }
 private static VolumeHealthResult QueryWindows(string path,CancellationToken ct) {
  ct.ThrowIfCancellationRequested();if(!OperatingSystem.IsWindows())return new(path,"","",VolumeHealthState.Unknown,"This readiness query requires Windows.");
  var mount=new StringBuilder(32768);if(!Native.GetVolumePathNameW(path,mount,(uint)mount.Capacity))return Failure(path,"","","Resolve volume",Marshal.GetLastPInvokeError());
  var root=mount.ToString();var filesystem=new StringBuilder(256);
  if(!Native.GetVolumeInformationW(root,null,0,out _,out _,out var flags,filesystem,(uint)filesystem.Capacity))return Failure(path,root,"","Read filesystem information",Marshal.GetLastPInvokeError());
  var format=filesystem.ToString();if((flags&0x00080000)!=0)return new(path,root,format,VolumeHealthState.ReadOnly,"Windows reports FILE_READ_ONLY_VOLUME.");
  if(!format.Equals("NTFS",StringComparison.OrdinalIgnoreCase))return new(path,root,format,VolumeHealthState.NotApplicable,$"{format} does not use the NTFS dirty flag queried by this guard; ordinary write and space checks still apply.");
  var volume=new StringBuilder(1024);if(!Native.GetVolumeNameForVolumeMountPointW(root,volume,(uint)volume.Capacity))return Failure(path,root,format,"Resolve local volume handle (remote shares do not expose this query)",Marshal.GetLastPInvokeError());
  ct.ThrowIfCancellationRequested();var device=volume.ToString().TrimEnd('\\');
  // DesiredAccess=0 queries attributes only. OPEN_EXISTING and shared reads/writes never alter or lock out the volume.
  using var handle=Native.CreateFileW(device,0,0x1|0x2,IntPtr.Zero,3,0,IntPtr.Zero);
  if(handle.IsInvalid)return Failure(path,root,format,"Open read-only volume query handle",Marshal.GetLastPInvokeError());
  const uint FsctlIsVolumeDirty=0x00090078;
  if(!Native.DeviceIoControl(handle,FsctlIsVolumeDirty,IntPtr.Zero,0,out var dirtyFlags,4,out var returned,IntPtr.Zero)) {
   var error=Marshal.GetLastPInvokeError();
   if(error!=1)return Failure(path,root,format,"Query NTFS dirty flag",error);
   // Some NTFS device stacks reject FSCTL on an attributes-only handle. Retry with GENERIC_READ only.
   // No GENERIC_WRITE, raw ReadFile call, lock, dismount, or repair control code is ever issued.
   handle.Dispose();using var readHandle=Native.CreateFileW(device,0x80000000,0x1|0x2,IntPtr.Zero,3,0,IntPtr.Zero);
   if(readHandle.IsInvalid)return Failure(path,root,format,"Open read-only filesystem query handle",Marshal.GetLastPInvokeError());
   if(!Native.DeviceIoControl(readHandle,FsctlIsVolumeDirty,IntPtr.Zero,0,out dirtyFlags,4,out returned,IntPtr.Zero))return Failure(path,root,format,"Query NTFS dirty flag",Marshal.GetLastPInvokeError());
  }
  ct.ThrowIfCancellationRequested();if(returned<4)return new(path,root,format,VolumeHealthState.Unknown,"Windows returned incomplete volume information.");
  return (dirtyFlags&1)!=0?new(path,root,format,VolumeHealthState.Dirty,"Existing transfers and backups should remain paused until the filesystem has been checked and this flag is clear."):new(path,root,format,VolumeHealthState.Clean,"FSCTL_IS_VOLUME_DIRTY returned a clear dirty flag.");
 }
 private static VolumeHealthResult Failure(string path,string volume,string format,string action,int error) {
  // ERROR_FILE_CORRUPT and ERROR_DISK_CORRUPT are affirmative filesystem failures, not permission failures.
  var state=error is 1392 or 1393?VolumeHealthState.Dirty:VolumeHealthState.Unknown;
  return new(path,volume,format,state,$"{action} failed (Windows {error}: {new Win32Exception(error).Message}).");
 }
 private static class Native {
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
  [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetVolumePathNameW(string fileName,StringBuilder volumePathName,uint bufferLength);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
  [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetVolumeInformationW(string rootPathName,StringBuilder? volumeNameBuffer,uint volumeNameSize,out uint volumeSerialNumber,out uint maximumComponentLength,out uint fileSystemFlags,StringBuilder fileSystemNameBuffer,uint fileSystemNameSize);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
  [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint,StringBuilder volumeName,uint bufferLength);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)]
  internal static extern SafeFileHandle CreateFileW(string fileName,uint desiredAccess,uint shareMode,IntPtr securityAttributes,uint creationDisposition,uint flagsAndAttributes,IntPtr templateFile);
  [DllImport("kernel32.dll",SetLastError=true,ExactSpelling=true)]
  [return:MarshalAs(UnmanagedType.Bool)]internal static extern bool DeviceIoControl(SafeFileHandle device,uint controlCode,IntPtr inBuffer,uint inBufferSize,out uint outBuffer,uint outBufferSize,out uint bytesReturned,IntPtr overlapped);
 }
}

