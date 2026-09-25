using ArcadeLibraryManager.Core;
using System.Xml.Linq;

public static class LaunchBoxLaunchArgumentChecks
{
    public static async Task<List<string>> RunAsync(string root)
    {
        var lb = Path.Combine(root, "LaunchBox");
        var tp = Path.Combine(root, "Emulator With Spaces", "TeknoParrot");
        Directory.CreateDirectory(Path.Combine(lb, "Data", "Platforms"));
        Directory.CreateDirectory(Path.Combine(tp, "UserProfiles"));
        Directory.CreateDirectory(tp); File.WriteAllText(Path.Combine(tp, "TeknoParrotUi.exe"), "inert executable fixture");
        var payload = Path.Combine(root, "fixture.bin"); File.WriteAllBytes(payload, [1]);
        var profile = Path.Combine(tp, "UserProfiles", "crusnusa.xml");
        new XElement("GameProfile", new XElement("GamePath", payload)).Save(profile);
        new XElement("LaunchBox").Save(Path.Combine(lb, "Data", "Platforms", "TeknoParrot.xml"));
        var settings = new AppSettings { LaunchBoxPath = lb, TeknoParrotPath = tp, PlatformName = "TeknoParrot", FillMissingMetadata = false };
        var game = new GameRecord { Id = "crusnusa", Name = "Cruis'n USA", UserProfilePath = profile };
        var emulator = new XElement("Emulator", new XElement("ID", "tekno-id"), new XElement("Title", "Custom title"),
            new XElement("ApplicationPath", "\"" + Path.GetRelativePath(lb, Path.Combine(tp, "TeknoParrotUi.exe")) + "\""),
            new XElement("CommandLine", "--profile="), new XElement("NoSpace", true),
            new XElement("NoQuotes", false), new XElement("FileNameWithoutExtensionAndPath", false));
        var xml = new XDocument(new XElement("LaunchBox", emulator));
        var path = Path.Combine(lb, "Data", "Emulators.xml");
        var service = new LaunchBoxService(() => false, VolumeHealthChecks.CleanProvider());
        async Task<LaunchBoxPlanItem> Preview()
        {
            xml.Save(path); var before = File.ReadAllBytes(path);
            var plan = await service.PreviewAsync(settings, [game]);
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "Game-sync preview modified emulator settings.");
            return plan.Items.Single();
        }
        var implicitAppend = await Preview();
        Check(implicitAppend.Action == "Add", "Working --profile= automatic append was rejected: " + implicitAppend.Detail);
        emulator.SetElementValue("NoSpace", false);
        var badSpacing = await Preview();
        Check(badSpacing.Action == "NeedsReview" && badSpacing.Detail.Contains("Setup", StringComparison.Ordinal), "Invalid append spacing did not produce an actionable Setup diagnosis.");
        emulator.SetElementValue("CommandLine", "unrelated-global-command");
        xml.Root!.Add(new XElement("EmulatorPlatform", new XElement("Emulator", "tekno-id"),
            new XElement("Platform", "TeknoParrot"), new XElement("CommandLine", "--profile=%romfile%"), new XElement("Default", true)));
        var platformOverride = await Preview();
        Check(platformOverride.Action == "Add", "A working associated-platform command was ignored: " + platformOverride.Detail);
        xml.Root.Element("EmulatorPlatform")!.SetElementValue("CommandLine", "--profile=%ROMNAME%");
        emulator.SetElementValue("CommandLine", "--profile=%romfile%");
        var badOverride = await Preview();
        Check(badOverride.Action == "NeedsReview", "A broken platform override was hidden by a valid global command.");
        return ["Crus'n USA adds with valid implicit --profile= and a quoted relative emulator path",
            "Game sync uses the effective associated-platform command and reports bad spacing/overrides without changing emulator XML"];
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception("Launch arguments: " + message); }
}
