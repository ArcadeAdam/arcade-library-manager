using ArcadeLibraryManager.Core;

public static class SetupDiscoveryChecks {
    static void Check(bool value,string message){if(!value)throw new Exception("Setup discovery: "+message);}
    static void Touch(string path){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"fixture");}
    public static async Task<List<string>> RunAsync(string root){
        var results=new List<string>();var lb=Path.Combine(root,"LaunchBox");var tp=Path.Combine(root,"Emulators","TeknoParrot");var mame=Path.Combine(root,"Emulators","MAME");
        Touch(Path.Combine(lb,"LaunchBox.exe"));Touch(Path.Combine(tp,"TeknoParrotUi.exe"));Directory.CreateDirectory(Path.Combine(tp,"GameProfiles"));
        Directory.CreateDirectory(Path.Combine(mame,"artwork"));Touch(Path.Combine(mame,"mame.exe"));Directory.CreateDirectory(Path.Combine(lb,"Data"));
        File.WriteAllText(Path.Combine(lb,"Data","Emulators.xml"),"<LaunchBox><Emulator><ID>tp-emu</ID><CommandLine>--profile=%ROMNAME%</CommandLine><Title>TeknoParrot</Title><ApplicationPath>../Emulators/TeknoParrot/TeknoParrotUi.exe</ApplicationPath></Emulator><Emulator><Title>MAME</Title><ApplicationPath>../Emulators/MAME/mame.exe</ApplicationPath></Emulator></LaunchBox>");
        File.WriteAllText(Path.Combine(lb,"Data","Platforms.xml"),"<LaunchBox><Platform><Name>TeknoParrot</Name></Platform><PlatformFolder><Platform>TeknoParrot</Platform><MediaType>Theme Video</MediaType><FolderPath>CustomThemes</FolderPath></PlatformFolder></LaunchBox>");
        Directory.CreateDirectory(Path.Combine(lb,"CustomThemes"));Touch(Path.Combine(lb,"ThirdParty","FFMPEG","ffmpeg.exe"));Touch(Path.Combine(lb,"ThirdParty","FFMPEG","ffprobe.exe"));
        var roms=Path.Combine(root,"roms");var destination=Path.Combine(roms,"TeknoParrot","Teknoparrot");Directory.CreateDirectory(Path.Combine(destination,"_downloads"));
        var settings=new AppSettings{LaunchBoxPath=lb,RomRoots=new(){roms},ThemeMotionStrength=73};
        var beforeFiles=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(p=>p).ToArray();
        var discovery=await new SetupDiscoveryService(false).DiscoverAsync(settings);
        Check(discovery.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.TeknoParrotPath)&&s.Value==tp),"relative emulator path was not resolved");
        Check(discovery.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.ThemeExamplesPath)&&s.Value==Path.Combine(lb,"CustomThemes")),"LaunchBox theme override was not respected");
        Check(discovery.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.DestinationPath)&&s.Value==destination),"nested ROM destination not found");
        Check(discovery.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.MameArtworkPath)&&s.Value==Path.Combine(mame,"artwork")),"MAME artwork not found");
        Check(discovery.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.FfmpegPath)),"existing ffmpeg pair not found");
        Check(settings.TeknoParrotPath==""&&settings.DestinationPath=="","read-only discovery mutated settings");
        Check(beforeFiles.SequenceEqual(Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).OrderBy(p=>p)),"discovery wrote files");
        Check(discovery.LaunchBoxEmulatorSetup?.CanRepair == true, "discovery did not expose the repairable TeknoParrot launch configuration");
        results.Add("Selected-folder discovery resolves emulator paths, media overrides, MAME artwork, tool pairs and nested game folders without writing");
        var applied=SetupDiscoveryService.ApplySuggestions(settings,discovery.Suggestions);
        Check(applied.TeknoParrotPath==tp&&applied.ThemeMotionStrength==settings.ThemeMotionStrength&&settings.TeknoParrotPath=="","proposal application did not copy or preserve settings");
        var custom=new AppSettings{TeknoParrotPath="X:\\Configured"};var preserved=SetupDiscoveryService.ApplySuggestions(custom,discovery.Suggestions);
        Check(preserved.TeknoParrotPath==custom.TeknoParrotPath,"configured location overwritten");
        Check(SetupDiscoveryService.ApplySuggestions(custom,discovery.Suggestions,false).TeknoParrotPath==tp,"explicit replacement did not apply");
        try{SetupDiscoveryService.ApplySuggestions(settings,new[]{new SetupSuggestion(nameof(AppSettings.ThemeMotionStrength),"99","invalid")});throw new Exception("unrelated setting mutation accepted");}catch(ArgumentException){}
        results.Add("Reviewed proposals preserve configured paths and account settings; unrecognized fields are rejected");
        var other=Path.Combine(root,"other-roms");Directory.CreateDirectory(Path.Combine(other,"TeknoParrot"));
        var ambiguous=await new SetupDiscoveryService(false).DiscoverAsync(new(){RomRoots=new(){roms,other}});
        Check(!ambiguous.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.DestinationPath))&&ambiguous.Warnings.Any(w=>w.Contains("Several new-game")),"ambiguous source selected silently");
        results.Add("Ambiguous destinations require a user choice instead of a guessed write location");
        File.WriteAllText(Path.Combine(lb,"Data","Emulators.xml"),"<!DOCTYPE LaunchBox [<!ENTITY e SYSTEM 'file:///must-not-be-read'>]><LaunchBox>&e;</LaunchBox>");
        var malformed=await new SetupDiscoveryService(false).DiscoverAsync(settings);
        Check(malformed.Warnings.Any(w=>w.Contains("Emulators.xml"))&&malformed.Suggestions.Any(s=>s.PropertyName==nameof(AppSettings.FfmpegPath)),"invalid XML did not isolate failure");
        results.Add("Invalid or external-entity XML is rejected while independent discovery continues");
        using var cts=new CancellationTokenSource();cts.Cancel();try{await new SetupDiscoveryService(false).DiscoverAsync(settings,cts.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
        results.Add("Discovery cancellation stops before further filesystem work");return results;
    }
}
