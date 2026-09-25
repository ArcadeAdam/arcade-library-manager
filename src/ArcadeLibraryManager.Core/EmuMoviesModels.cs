namespace ArcadeLibraryManager.Core;

public sealed record EmuMoviesPlatformChoice(string Name, string MatchFolder, int GameCount);

public sealed class EmuMoviesVideoRequest
{
    public string LaunchBoxPath { get; set; } = "";
    public string PlatformName { get; set; } = "TeknoParrot";
    public string MatchFolder { get; set; } = "";
    public string Catalog { get; set; } = "ArcadePC";
    public string WorkDirectory { get; set; } = "";
    public string SyncExecutablePath { get; set; } = "";
    public bool UseMameFallback { get; set; } = true;
}

public sealed class EmuMoviesVideoRunResult
{
    public int Downloaded { get; set; }
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int Missing { get; set; }
    public string ReportDirectory { get; set; } = "";
    public List<string> Details { get; set; } = [];
}
