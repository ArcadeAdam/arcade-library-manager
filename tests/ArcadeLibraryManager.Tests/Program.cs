using System.Text.Json;
using ArcadeLibraryManager.Core;
try {
 if(args.Length>1 && args[0]=="--scan"){
  var settings=new AppSettings{TeknoParrotPath=args[1]};
  var workspace=Path.Combine(Directory.GetCurrentDirectory(),".artifacts","read-only-scan");
  var games=await new AppEngine(settings,workspace).ScanAsync(default);
  var report=new{ReleasedProfiles=games.Count,Installed=games.Count(g=>g.Installed),PathsValid=games.Count(g=>g.PathsValid),Missing=games.Count(g=>!g.Installed),Sample=games.Take(4).Select(g=>new{g.Id,g.Name,g.Status})};
  Directory.CreateDirectory(workspace);File.WriteAllText(Path.Combine(workspace,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return 0;
 }
 var root=Path.GetFullPath(args.FirstOrDefault(a=>!a.StartsWith("--"))??Path.Combine(AppContext.BaseDirectory,"fixtures",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
 Directory.CreateDirectory(root);
 var results=new Dictionary<string,object>();
 results["EmuMovies"]=await EmuMoviesChecks.RunAsync(Path.Combine(root,"emumovies"));
 results["GameSelection"]=GameSelectionChecks.Run();
 results["LibraryGenreFilter"]=LibraryGenreFilterChecks.Run();
 results["LaunchBoxTransactions"] = await LaunchBoxTransactionChecks.RunAsync(Path.Combine(root,"launchbox-transactions"));
 results["Core"]=await CoreChecks.RunAsync(Path.Combine(root,"core"));
 results["DependencyInstall"]=await DependencyInstallChecks.RunAsync(Path.Combine(root,"dependency-install"));
 results["OverRevRecipe"]=OverRevRecipeChecks.Run(Path.Combine(root,"overrev-recipe"));
 results["MissingArchiveMapping"]=await MissingArchiveMappingChecks.RunAsync(Path.Combine(root,"missing-archive-mapping"));
 await InstallSelectionChecks.RunAsync(Path.Combine(root,"single-version"));results["InstallSelection"]="Passed region preference, availability fallback, repeat/checkpoint enforcement and workflow exclusion";
 results["LaunchBoxLaunchArguments"]=await LaunchBoxLaunchArgumentChecks.RunAsync(Path.Combine(root,"launchbox-launch-arguments"));
 results["LaunchBoxEmulatorSetup"]=await LaunchBoxEmulatorSetupChecks.RunAsync(Path.Combine(root,"launchbox-emulator-setup"));
 results["SetupDiscovery"]=await SetupDiscoveryChecks.RunAsync(Path.Combine(root,"discovery"));
 await VolumeHealthChecks.RunAsync(Path.Combine(root,"volume"));results["VolumeHealth"]="Passed dirty/unknown/clean, permissions and cancellation guards";
 await LaunchBoxChecks.RunAsync(Path.Combine(root,"launchbox"));results["LaunchBox"]="Passed all integration assertions";
 await LaunchBoxRelatedChecks.RunAsync(Path.Combine(root,"related-playlists"));results["LaunchBoxRelated"]="Passed playlist remapping, selected group isolation, deduplication, exact multi-file undo and external/custom reference guards";
 await BezelChecks.RunAsync(Path.Combine(root,"bezel"));results["Bezels"]="Passed all integration assertions";
 await WorkflowChecks.RunAsync(Path.Combine(root,"workflow"));results["Workflow"]="Passed durable resume, stage guards and service integration assertions";
 results["Updater"]=await UpdaterChecks.RunAsync(Path.Combine(root,"updater"));
 results["ThemeEncoding"]=await ThemeEncodingChecks.RunAsync(Path.Combine(root,"theme-encoding"));
 results["ThemeBatchProgress"]=ThemeBatchProgressChecks.Run();
 results["ThemeTools"]=await ThemeToolsChecks.RunAsync(Path.Combine(root,"theme-tools"));
 var ffmpeg=Environment.GetEnvironmentVariable("ALM_TEST_FFMPEG");
 results["Media"]=await MediaChecks.RunAsync(Path.Combine(root,"media"),ffmpeg);
 results["Fanart"]=await FanartChecks.RunAsync(Path.Combine(root,"fanart"),ffmpeg);
 results["ThemeCutouts"]=await ThemeCutoutChecks.RunAsync(Path.Combine(root,"theme-cutouts"),ffmpeg);
 results["MediaDiscovery"]=await MediaDiscoveryChecks.RunAsync(Path.Combine(root,"media-discovery"));
 results["RelatedThemeDiscovery"]=RelatedThemeDiscoveryChecks.Run(Path.Combine(root,"related-theme-discovery"));
 results["ThemeReuse"]=await ThemeReuseChecks.RunAsync(Path.Combine(root,"theme-reuse"));
 results["ThemeReuseIntegration"]=await ThemeReuseIntegrationChecks.RunAsync(Path.Combine(root,"theme-reuse-integration"));
 var json=JsonSerializer.Serialize(new{Passed=true,CompletedUtc=DateTime.UtcNow,Results=results},new JsonSerializerOptions{WriteIndented=true});
 File.WriteAllText(Path.Combine(root,"results.json"),json);Console.WriteLine(json);return 0;
} catch(Exception ex) { Console.Error.WriteLine(ex.ToString());return 1; }



