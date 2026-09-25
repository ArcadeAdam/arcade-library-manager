using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

public sealed record SetupSuggestion(string PropertyName, string Value, string Reason);
public sealed record SetupDiscoveryResult(List<SetupSuggestion> Suggestions, List<string> Warnings)
{
    public LaunchBoxEmulatorSetupPlan? LaunchBoxEmulatorSetup { get; init; }
}

/// <summary>Read-only discovery from configured folders and a small set of known adjacent locations. Never inventories a ROM library.</summary>
public sealed class SetupDiscoveryService(bool includeSystemTools = true)
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        nameof(AppSettings.TeknoParrotPath), nameof(AppSettings.LaunchBoxPath), nameof(AppSettings.PlatformName),
        nameof(AppSettings.DestinationPath), nameof(AppSettings.CachePath), nameof(AppSettings.ThemeExamplesPath),
        nameof(AppSettings.MameArtworkPath), nameof(AppSettings.FfmpegPath), nameof(AppSettings.SevenZipPath)
    };

    public Task<SetupDiscoveryResult> DiscoverAsync(AppSettings settings, CancellationToken ct = default) => Task.Run(() => Discover(settings, ct), ct);

    private SetupDiscoveryResult Discover(AppSettings source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = Clone(source);
        var suggestions = new List<SetupSuggestion>();
        var warnings = new List<string>();
        void Check() => ct.ThrowIfCancellationRequested();
        bool Folder(string path) { Check(); try { return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; } }
        bool FilePresent(string path) { Check(); try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; } }
        bool TeknoParrot(string path) => Folder(path) && Folder(Path.Combine(path,"GameProfiles")) && FilePresent(Path.Combine(path,"TeknoParrotUi.exe"));
        bool LaunchBox(string path) => Folder(path) && Folder(Path.Combine(path,"Data")) && FilePresent(Path.Combine(path,"LaunchBox.exe"));
        string Resolve(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root,path));
        string? Parent(string path) { try { return string.IsNullOrWhiteSpace(path) ? null : Directory.GetParent(Path.GetFullPath(path))?.FullName; } catch (Exception e) when (e is ArgumentException or IOException) { return null; } }
        void Suggest(string key,string value,string reason) {
            if(string.IsNullOrWhiteSpace(value) || suggestions.Any(s=>s.PropertyName==key)) return;
            var old=typeof(AppSettings).GetProperty(key)?.GetValue(source)?.ToString()??"";
            if(old.Equals(value,StringComparison.OrdinalIgnoreCase)) return;
            suggestions.Add(new(key,value,reason));
        }
        string SelectUnique(IEnumerable<string> candidates,string field,Func<string,bool> valid) {
            var found=candidates.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(valid).ToList();
            if(found.Count==1) return found[0];
            if(found.Count>1) warnings.Add($"Several {field} locations match. Select one manually: {string.Join("; ",found)}");
            return "";
        }
        XDocument? ReadXml(string path) {
            if(!FilePresent(path)) return null;
            try { using var reader=XmlReader.Create(path,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null }); return XDocument.Load(reader); }
            catch(Exception ex) when(ex is XmlException or IOException or UnauthorizedAccessException) { warnings.Add($"Could not inspect {Path.GetFileName(path)}: {ex.Message}"); return null; }
        }
        var tp=settings.TeknoParrotPath;
        var lb=settings.LaunchBoxPath;
        if(!string.IsNullOrWhiteSpace(tp) && !TeknoParrot(tp)) warnings.Add("The configured TeknoParrot folder needs GameProfiles and TeknoParrotUi.exe.");
        if(!string.IsNullOrWhiteSpace(lb) && !LaunchBox(lb)) warnings.Add("The configured LaunchBox folder needs Data and LaunchBox.exe.");
        if(string.IsNullOrWhiteSpace(lb) && TeknoParrot(tp)) {
            var candidates=new List<string>();
            var parent=Parent(tp);var grand=parent is null?null:Parent(parent);
            if(parent!=null)candidates.Add(Path.Combine(parent,"LaunchBox"));
            if(grand!=null)candidates.Add(Path.Combine(grand,"LaunchBox"));
            lb=SelectUnique(candidates,"LaunchBox",LaunchBox);
            if(lb!="") {settings.LaunchBoxPath=lb;Suggest(nameof(AppSettings.LaunchBoxPath),lb,"LaunchBox installation beside the configured emulator collection.");}
        }
        XDocument? emulatorDocument=null;
        if(LaunchBox(lb)) emulatorDocument=ReadXml(Path.Combine(lb,"Data","Emulators.xml"));
        if(string.IsNullOrWhiteSpace(tp) && LaunchBox(lb)) {
            var candidates=new List<string>();
            foreach(var emulator in emulatorDocument?.Descendants("Emulator")??[]) {
                Check();var name=(emulator.Element("Title")?.Value??emulator.Element("Name")?.Value??"").Trim();
                var app=emulator.Element("ApplicationPath")?.Value??"";
                if(!name.Contains("TeknoParrot",StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(app).Equals("TeknoParrotUi.exe",StringComparison.OrdinalIgnoreCase)) continue;
                try {if(app!="")candidates.Add(Path.GetDirectoryName(Resolve(lb,app))!);} catch(Exception e) when(e is ArgumentException or IOException) {warnings.Add("A LaunchBox emulator path could not be resolved.");}
            }
            candidates.Add(Path.Combine(lb,"Emulators","TeknoParrot"));
            var parent=Parent(lb);if(parent!=null)candidates.Add(Path.Combine(parent,"Emulators","TeknoParrot"));
            tp=SelectUnique(candidates,"TeknoParrot",TeknoParrot);
            if(tp!="") {settings.TeknoParrotPath=tp;Suggest(nameof(AppSettings.TeknoParrotPath),tp,"TeknoParrot installation referenced by LaunchBox or its known emulator folder.");}
        }
        if(LaunchBox(lb)) {
            var platforms=ReadXml(Path.Combine(lb,"Data","Platforms.xml"));
            var names=(platforms?.Descendants("Platform").Select(e=>e.Element("Name")?.Value??"")??[]).Where(n=>n.Contains("TeknoParrot",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var exact=names.FirstOrDefault(n=>n.Equals(settings.PlatformName,StringComparison.OrdinalIgnoreCase));
            if(exact!=null)settings.PlatformName=exact;
            else if(names.Count==1){settings.PlatformName=names[0];Suggest(nameof(AppSettings.PlatformName),names[0],"Existing TeknoParrot platform in LaunchBox.");}
            else if(names.Count>1)warnings.Add("Several TeknoParrot platforms exist; choose the platform name before syncing.");
            try {
                var folders=LaunchBoxService.DiscoverMediaFolders(settings);
                var themes=folders.TryGetValue("Theme Video",out var configured)?configured:folders.TryGetValue("Video",out var video)?Path.Combine(video,"Theme"):Path.Combine(lb,"Videos",settings.PlatformName,"Theme");
                if(Folder(themes))Suggest(nameof(AppSettings.ThemeExamplesPath),Path.GetFullPath(themes),"Existing LaunchBox theme folder, including platform folder overrides.");
            }catch(Exception ex) when(ex is IOException or XmlException or ArgumentException) {warnings.Add("LaunchBox media folder discovery needs review: "+ex.Message);}
            var ffmpeg=Path.Combine(lb,"ThirdParty","FFMPEG","ffmpeg.exe");
            if(FilePresent(ffmpeg) && FilePresent(Path.Combine(Path.GetDirectoryName(ffmpeg)!,"ffprobe.exe")))Suggest(nameof(AppSettings.FfmpegPath),ffmpeg,"LaunchBox FFmpeg and ffprobe pair; no tool files are copied.");
            else if(FilePresent(ffmpeg))warnings.Add("LaunchBox FFmpeg exists but its companion ffprobe.exe is missing.");
        }
        var destinations=new List<string>();
        foreach(var root in source.RomRoots.Where(r=>!string.IsNullOrWhiteSpace(r))) {
            Check();if(!Folder(root))continue;
            foreach(var candidate in new[]{Path.Combine(root,"TeknoParrot","Teknoparrot"),Path.Combine(root,"TeknoParrot")})if(Folder(candidate))destinations.Add(Path.GetFullPath(candidate));
            if(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)).Equals("TeknoParrot",StringComparison.OrdinalIgnoreCase)) {
                var nested=Path.Combine(root,"Teknoparrot");destinations.Add(Path.GetFullPath(Folder(nested)?nested:root));
            }
        }
        destinations=destinations.Distinct(StringComparer.OrdinalIgnoreCase).Where(p=>!destinations.Any(other=>other.Length>p.Length&&other.StartsWith(p.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))).ToList();
        if(string.IsNullOrWhiteSpace(settings.DestinationPath)) {
            var destination=SelectUnique(destinations,"new-game destination",Folder);
            if(destination!=""){settings.DestinationPath=destination;Suggest(nameof(AppSettings.DestinationPath),destination,"Existing TeknoParrot folder directly under a configured ROM source.");}
        }
        if(Folder(settings.DestinationPath)) {
            var cache=new[]{Path.Combine(settings.DestinationPath,".alm-downloads"),Path.Combine(settings.DestinationPath,"_downloads")}.FirstOrDefault(Folder);
            if(cache!=null)Suggest(nameof(AppSettings.CachePath),cache,"Existing download cache under the game destination.");
        }
        var mameCandidates=new List<string>();
        var emulatorParent=Parent(tp);if(emulatorParent!=null)mameCandidates.Add(Path.Combine(emulatorParent,"MAME","artwork"));
        if(emulatorDocument!=null)foreach(var emulator in emulatorDocument.Descendants("Emulator")) {
            Check();var app=emulator.Element("ApplicationPath")?.Value??"";
            if(!Path.GetFileName(app).Equals("mame.exe",StringComparison.OrdinalIgnoreCase)&&!Path.GetFileName(app).Equals("mame64.exe",StringComparison.OrdinalIgnoreCase))continue;
            try {if(app!="")mameCandidates.Add(Path.Combine(Path.GetDirectoryName(Resolve(lb,app))!,"artwork"));}catch(Exception e)when(e is ArgumentException or IOException){warnings.Add("A LaunchBox MAME path could not be resolved.");}
        }
        var artwork=SelectUnique(mameCandidates,"MAME artwork",Folder);
        if(artwork!="")Suggest(nameof(AppSettings.MameArtworkPath),artwork,"MAME artwork beside its configured emulator executable.");
        if(includeSystemTools) {
            var toolCandidates=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)}.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>Path.Combine(p,"7-Zip","7z.exe"));
            var tool=toolCandidates.FirstOrDefault(FilePresent);
            if(tool!=null)Suggest(nameof(AppSettings.SevenZipPath),tool,"Installed 7-Zip executable (reserved for broader archive support).");
        }
        if(suggestions.Count==0)warnings.Add("No missing locations were discovered. Choose folders in Setup; discovery does not search entire drives.");
        Check();
        var launchSetup = !string.IsNullOrWhiteSpace(settings.LaunchBoxPath) && !string.IsNullOrWhiteSpace(settings.TeknoParrotPath)
            ? new LaunchBoxEmulatorSetupService().Inspect(settings, ct) : null;
        return new(suggestions,warnings) { LaunchBoxEmulatorSetup = launchSetup };
    }
    public static AppSettings ApplySuggestions(AppSettings settings,IEnumerable<SetupSuggestion> suggestions,bool fillOnlyEmpty=true)
    {
        ArgumentNullException.ThrowIfNull(settings);ArgumentNullException.ThrowIfNull(suggestions);
        var copy=Clone(settings);
        foreach(var suggestion in suggestions) {
            if(!Allowed.Contains(suggestion.PropertyName))throw new ArgumentException("Unsupported discovery field: "+suggestion.PropertyName);
            if(string.IsNullOrWhiteSpace(suggestion.Value))continue;
            var property=typeof(AppSettings).GetProperty(suggestion.PropertyName)!;
            if(fillOnlyEmpty&&!string.IsNullOrWhiteSpace(property.GetValue(copy)?.ToString()))continue;
            property.SetValue(copy,suggestion.Value);
        }
        return copy;
    }
    private static AppSettings Clone(AppSettings settings)=>JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings,SettingsStore.Json),SettingsStore.Json)!;
}
