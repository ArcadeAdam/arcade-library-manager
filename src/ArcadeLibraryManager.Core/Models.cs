namespace ArcadeLibraryManager.Core;
public sealed class AppSettings {
 public List<string> RomRoots {get;set;} = new();
 public string TeknoParrotPath {get;set;} = "";
 public string LaunchBoxPath {get;set;} = "";
 public string PlatformName {get;set;} = "TeknoParrot";
 public string DestinationPath {get;set;} = "";
 public string ArchiveUrl {get;set;} = "https://archive.org/details/teknoparrot-collection_";
 public string CachePath {get;set;} = "";
 public string ThemeExamplesPath {get;set;} = "";
 public string FfmpegPath {get;set;} = "";
 public string SevenZipPath {get;set;} = "";
 public bool FillMissingMetadata {get;set;} = true;
 public int MaxDownloadMbps {get;set;} = 0;
 public int VideoWidth {get;set;} = 1280;
 public int VideoHeight {get;set;} = 720;
 public int VideoFps {get;set;} = 30;
 public int VideoSeconds {get;set;} = ThemeDuration.DefaultSeconds;
 public string ThemeLayout {get;set;} = "Fanart";
 public string ThemeAssetsPath {get;set;} = "";
 public string ThemeCutoutModelPath {get;set;} = "";
 public bool ThemeAutoCutouts {get;set;} = true;
 public string ThemeVideoSide {get;set;} = "Right";
 public int ThemeMotionStrength {get;set;} = 60;
 public bool ThemeAudio {get;set;} = true;
 public string MameArtworkPath {get;set;} = "";
 public string BezelImportPath {get;set;} = "";
 public string BezelSourceUrl {get;set;} = "https://discord.com/channels/284830696860680192/1033793318460588053";
}
public sealed class GameRecord {
 public string Id {get;set;} = "";
 public string Name {get;set;} = "";
 public string Emulator {get;set;} = "";
 public string TemplatePath {get;set;} = "";
 public string UserProfilePath {get;set;} = "";
 public string GamePath {get;set;} = "";
 public string GamePath2 {get;set;} = "";
 public string ExecutableName {get;set;} = "";
 public string ExecutableName2 {get;set;} = "";
 public bool HasTwoExecutables {get;set;}
 public bool Installed {get;set;}
 public bool PathsValid {get;set;}
 public bool SubscriptionRequired {get;set;}
 public string Notes {get;set;} = "";
 public string Status {get;set;} = "";
 public string LaunchBoxStatus {get;set;} = "";
 public string ArtworkStatus {get;set;} = "";
 public string ThemeStatus {get;set;} = "";
 public string Genre {get;set;} = "";
 public string Year {get;set;} = "";
}
public sealed class GameRecipe {
 public string Id {get;set;} = "";
 public string Name {get;set;} = "";
 public string PayloadId {get;set;} = "";
 public string ArchiveItem {get;set;} = "";
 public string ArchiveName {get;set;} = "";
 public long ArchiveSize {get;set;}
 public string ArchiveMd5 {get;set;} = "";
 public string ArchiveSha1 {get;set;} = "";
 public string PrimaryPath {get;set;} = "";
 public string SecondaryPath {get;set;} = "";
 public string RomSet {get;set;} = "";
 public List<RomRequirement> Roms {get;set;} = new();
 public List<DiskRequirement> Disks {get;set;} = new();
 public List<string> DependencySets {get;set;} = new();
 public string Notes {get;set;} = "";
}
public sealed class RomRequirement {
 public string Name {get;set;} = ""; public long Size {get;set;} public string Crc {get;set;} = ""; public string Sha1 {get;set;} = "";
}
public sealed class DiskRequirement {
 public string Name {get;set;} = ""; public string Sha1 {get;set;} = "";
}
public sealed class InstallPlanItem {
 public string ProfileId {get;set;} = ""; public string Name {get;set;} = "";
 public string Action {get;set;} = ""; public string Detail {get;set;} = "";
 public string PrimaryPath {get;set;} = ""; public string SecondaryPath {get;set;} = "";
 public string ArchivePath {get;set;} = ""; public string DownloadUrl {get;set;} = "";
 public string Destination {get;set;} = ""; public string TemplateHash {get;set;} = "";
 public long DownloadBytes {get;set;} public string OriginalProfileHash {get;set;} = ""; public List<string> LocalDependencies {get;set;} = new(); public GameRecipe? Recipe {get;set;}
 public bool Selected {get;set;} = true;
}
public sealed record JobEvent(string Stage,string Message,string GameId="",double? Percent=null);
public sealed record OperationResult(int Succeeded,int Skipped,List<string> Errors);
public sealed record JobRecord(string Id,string Name,string Stage,string Message,string UpdatedUtc);
public sealed class MediaAssets {
 public string ProfileId {get;set;} = ""; public string Title {get;set;} = ""; public string Background {get;set;} = "";
 public string Logo {get;set;} = ""; public string Snap {get;set;} = ""; public string Theme {get;set;} = "";
 public string ThemeDestination {get;set;} = "";
 public string ReusableTheme {get;set;} = "";
 public string ReusableThemePlatform {get;set;} = "";
 public string ThemeReuseDetail {get;set;} = "";
 public string ThemeReuseDestination {get;set;} = "";
 public long ReusableThemeLength {get;set;}
 public DateTime ReusableThemeLastWriteUtc {get;set;}

 public string BackgroundKind {get;set;} = "";
 public List<string> ArtworkSources {get;set;} = new();
 public List<string> Cutouts {get;set;} = new();
 public string ThemeAssetDirectory {get;set;} = "";
}


