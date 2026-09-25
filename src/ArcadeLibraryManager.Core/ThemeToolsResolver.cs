namespace ArcadeLibraryManager.Core;

/// <summary>Locates a colocated FFmpeg/ffprobe pair without launching tools or changing settings.</summary>
public sealed class ThemeToolsResolver
{
    private readonly string applicationDirectory;
    private const string SetupAdvice = "Open Setup and set 'FFmpeg executable' to ffmpeg.exe from a complete FFmpeg folder containing both ffmpeg.exe and ffprobe.exe, then retry Make themes.";

    public ThemeToolsResolver(string? applicationDirectory = null)
        => this.applicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);

    public MediaTools? Find(AppSettings settings) => Resolve(settings).Tools;
    public string GetError(AppSettings settings) => Resolve(settings).Error;
    public MediaTools Require(AppSettings settings)
    {
        var result = Resolve(settings);
        return result.Tools ?? throw new InvalidOperationException(result.Error);
    }

    private (MediaTools? Tools, string Error) Resolve(AppSettings settings)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? selectedError = null;
        void Add(string path) { if (seen.Add(path)) candidates.Add(path); }
        void Folder(string path) { Add(Path.Combine(path, "ffmpeg.exe")); Add(Path.Combine(path, "bin", "ffmpeg.exe")); }
        static string Clean(string value) => value.Trim().Trim('"').Trim();
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
        {
            try
            {
                var configured = Path.GetFullPath(Clean(settings.FfmpegPath));
                if (Directory.Exists(configured)) Folder(configured);
                else if (Path.GetFileName(configured).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                    Folder(Path.GetDirectoryName(configured)!);
                else if (Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) || File.Exists(configured)) Add(configured);
                else Folder(configured);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { selectedError = "The configured FFmpeg path is invalid or inaccessible: " + ex.Message; }
        }
        if (!string.IsNullOrWhiteSpace(settings.LaunchBoxPath))
        {
            try { Folder(Path.Combine(Path.GetFullPath(Clean(settings.LaunchBoxPath)), "ThirdParty", "FFMPEG")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { selectedError ??= "The LaunchBox path could not be checked for media tools: " + ex.Message; }
        }
        Folder(Path.Combine(applicationDirectory, "Tools"));
        Folder(Path.Combine(applicationDirectory, "Tools", "ffmpeg"));
        string? incompletePair = null, missingEncoder = null;
        foreach (var ffmpeg in candidates)
        {
            var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            var encoderState = State(ffmpeg); var probeState = State(probe);
            if (encoderState == "present" && probeState == "present") return (new(ffmpeg, probe), "");
            if (encoderState == "present") incompletePair ??= $"FFmpeg was found at '{ffmpeg}', but its required companion '{probe}' is {probeState}.";
            else if (probeState == "present") missingEncoder ??= $"ffprobe.exe was found at '{probe}', but ffmpeg.exe at '{ffmpeg}' is {encoderState}.";
            else if (encoderState != "missing") missingEncoder ??= $"The FFmpeg executable at '{ffmpeg}' is {encoderState}.";
        }
        var detail = incompletePair ?? missingEncoder ?? selectedError ?? $"Neither ffmpeg.exe nor ffprobe.exe was found at '{Path.GetDirectoryName(candidates[0])}' or in the other configured tool locations.";
        return (null, detail + " " + SetupAdvice);
    }

    private static string State(string path)
    {
        try { return !File.Exists(path) ? "missing" : new FileInfo(path).Length > 0 ? "present" : "empty"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unreadable"; }
    }
}
