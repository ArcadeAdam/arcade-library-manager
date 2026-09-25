using ArcadeLibraryManager.Core;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class ThemeCutoutChecks
{
    public static async Task<List<string>> RunAsync(string root, string? ffmpeg)
    {
        Directory.CreateDirectory(root); var passed = new List<string>();
        var alpha = Path.Combine(root, "character's art [50%].png"); var opaque = Path.Combine(root, "opaque.png");
        WritePng(alpha, true); WritePng(opaque, false);
        Require(ThemeCutoutService.HasUsableTransparency(alpha) && !ThemeCutoutService.HasUsableTransparency(opaque), "Actual alpha must distinguish transparent and opaque PNGs.");
        passed.Add("Actual alpha validation accepts subject art and rejects an opaque RGBA PNG");
        if (string.IsNullOrWhiteSpace(ffmpeg)) { passed.Add("SKIP: Cutout render checks need ALM_TEST_FFMPEG"); return passed; }
        var settings = new AppSettings { FfmpegPath = ffmpeg, CachePath = Path.Combine(root, "cache"), ThemeAutoCutouts = false };
        var service = new ThemeCutoutService(new VolumeHealthService((path, ct) => Task.FromResult(new VolumeHealthResult(path, Path.GetPathRoot(path)!, "NTFS", VolumeHealthState.Clean, "Fixture clean"))));
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(alpha));
        var output = Path.Combine(root, "prepared");
        var files = await service.PrepareAsync(settings, [alpha, opaque], output);
        Require(files.Count == 1 && ThemeCutoutService.HasUsableTransparency(files[0]), "Manual transparent art must work without model installation; opaque art must not become a cutout.");
        Require(SHA256.HashData(await File.ReadAllBytesAsync(alpha)).SequenceEqual(originalHash), "Cutout preparation modified source art.");
        var probe = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach(var arg in new[]{"-v","error","-show_entries","stream=width,height","-of","json",files[0]}) probe.ArgumentList.Add(arg);
        using (var process = Process.Start(probe)!) { var json = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); using var doc = JsonDocument.Parse(json); var stream = doc.RootElement.GetProperty("streams")[0]; Require(stream.GetProperty("width").GetInt32() == 38 && stream.GetProperty("height").GetInt32() == 54, "Transparent margins were not trimmed with safe padding."); }
        passed.Add("Local alpha preparation trims margins, preserves RGBA and leaves the source unchanged without any model");
        var stamp = File.GetLastWriteTimeUtc(files[0]);
        var repeat = await service.PrepareAsync(settings, [alpha, opaque], output);
        Require(repeat.SequenceEqual(files) && File.GetLastWriteTimeUtc(files[0]) == stamp, "Repeat preparation did not reuse immutable cached cutouts.");
        passed.Add("Repeat cutout preparation reuses the source-content cache");
        var cancelledDirectory = Path.Combine(root, "cancelled"); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false; try { await service.PrepareAsync(settings, [alpha], cancelledDirectory, ct:cancelled.Token); } catch(OperationCanceledException) { stopped = true; }
        Require(stopped && !Directory.Exists(cancelledDirectory), "Cancellation created partial output.");
        passed.Add("Pre-cancelled preparation leaves no partial output");
        settings.ThemeAutoCutouts = true; settings.ThemeCutoutModelPath = Path.Combine(root, "corrupt.onnx"); await File.WriteAllTextAsync(settings.ThemeCutoutModelPath, "corrupt");
        bool rejected = false; try { await service.PrepareAsync(settings, [opaque], output); } catch(InvalidDataException) { rejected = true; }
        Require(rejected && !File.Exists(Path.Combine(settings.CachePath,"Models",ThemeCutoutService.ModelFileName)), "Corrupt model was accepted or rendering started an unexpected download.");
        passed.Add("Corrupt models are rejected before inference and renders never auto-download a model");
        return passed;
    }
    private static void Require(bool value, string message) { if(!value) throw new InvalidOperationException(message); }
    private static void WritePng(string path, bool transparent)
    {
        using var stream=File.Create(path); stream.Write(new byte[]{137,80,78,71,13,10,26,10}); var header=new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4),64); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),64); header[8]=8; header[9]=6; Chunk(stream,"IHDR",header);
        using var data=new MemoryStream(); using(var zlib=new ZLibStream(data,CompressionLevel.Fastest,true)) for(int y=0;y<64;y++){zlib.WriteByte(0);for(int x=0;x<64;x++)zlib.Write(new byte[]{25,220,170,!transparent||(x>=16&&x<48&&y>=8&&y<56)?(byte)255:(byte)0});}
        Chunk(stream,"IDAT",data.ToArray());Chunk(stream,"IEND",[]);
    }
    private static void Chunk(Stream stream,string kind,byte[] data)
    {
        byte[] size=new byte[4];BinaryPrimitives.WriteInt32BigEndian(size,data.Length);stream.Write(size);var type=Encoding.ASCII.GetBytes(kind);stream.Write(type);stream.Write(data);uint crc=uint.MaxValue;foreach(var value in type.Concat(data)){crc^=value;for(int bit=0;bit<8;bit++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0u);}BinaryPrimitives.WriteUInt32BigEndian(size,~crc);stream.Write(size);
    }
}
