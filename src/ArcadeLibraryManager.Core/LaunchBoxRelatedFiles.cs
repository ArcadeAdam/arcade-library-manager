using System.Xml.Linq;

namespace ArcadeLibraryManager.Core;

internal static class LaunchBoxRelatedFiles
{
    private static readonly StringComparer IgnoreCase=StringComparer.OrdinalIgnoreCase;
    private static bool Relevant(LaunchBoxPlanItem item)=>item.Action is "Combine" or "Consolidate" || item.Action=="Update"&&item.Changes.ContainsKey("ApplicationPath");
    private static List<string> Inventory(string platform)
    {
        var data=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(platform)!,".."));
        return new[]{data,Path.Combine(data,"Playlists"),Path.Combine(data,"Platforms")}.Where(Directory.Exists)
            .SelectMany(d=>Directory.EnumerateFiles(d,"*.xml",SearchOption.TopDirectoryOnly)).Select(Path.GetFullPath)
            .Where(p=>!p.Equals(Path.GetFullPath(platform),StringComparison.OrdinalIgnoreCase)).Distinct(IgnoreCase).Order(IgnoreCase).ToList();
    }
    private static bool IsPlaylist(string path,string platform)=>Path.GetDirectoryName(path)!.Equals(
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(platform)!,"..","Playlists")),StringComparison.OrdinalIgnoreCase);
    internal static void AssertInventory(LaunchBoxPlan plan)
    {
        if(plan.ExternalDataInventory!=null&&!plan.ExternalDataInventory.SequenceEqual(Inventory(plan.SourcePath),IgnoreCase))
            throw new InvalidOperationException("LaunchBox data files were added or removed after preview. Preview again before changing game identities.");
    }
    internal static void Prepare(LaunchBoxPlan plan)
    {
        plan.RelatedDocuments.Clear();plan.GameIdRemaps.Clear();
        var candidates=plan.Items.Where(Relevant).ToList();if(candidates.Count==0)return;
        plan.ExternalDataInventory=Inventory(plan.SourcePath);
        var sources=new Dictionary<string,string>(IgnoreCase);
        foreach(var path in plan.ExternalDataInventory)
        {
            var hash=LaunchBoxService.Hash(path);var contents=LaunchBoxService.ReadXml(path).ToString(SaveOptions.DisableFormatting);
            if(hash!=LaunchBoxService.Hash(path))throw new InvalidOperationException("LaunchBox data changed during playlist preview: "+path);
            plan.SourceHashes[path]=hash;sources[path]=contents;
        }
        // A blocked group must not contribute rewrites to another group's reviewed playlists.
        while(true)
        {
            candidates=plan.Items.Where(Relevant).ToList();
            var blocked=FindUnsupported(plan,candidates,sources);
            if(blocked.Count==0)
            {
                foreach(var pair in BuildDocuments(plan,candidates,sources,out blocked))plan.RelatedDocuments[pair.Key]=pair.Value;
                if(blocked.Count==0)break;
                plan.RelatedDocuments.Clear();
            }
            foreach(var problem in blocked)
            { problem.Key.Action="NeedsReview";problem.Key.Selected=false;problem.Key.Detail=problem.Value; }
        }
        foreach(var item in candidates)
        {
            foreach(var id in item.RemovedGameIds)plan.GameIdRemaps[id]=item.GameGuid;
            var playlists=plan.RelatedDocuments.Keys.Where(path=>item.RemovedGameIds.Append(item.GameGuid).Any(id=>Contains(sources[path],id))).Select(Path.GetFileName).ToArray();
            if(playlists.Length>0)item.Detail+=" Update playlist references and membership details: "+string.Join(", ",playlists)+". Affected playlists are backed up with the platform.";
        }
        AssertInventory(plan);
    }
    internal static Dictionary<string,XDocument> PrepareCommit(LaunchBoxPlan plan,IReadOnlyList<LaunchBoxPlanItem> selected)
    {
        if(plan.ExternalDataInventory==null)return new(IgnoreCase);
        AssertInventory(plan);
        foreach(var pair in plan.SourceHashes)
            if(!File.Exists(pair.Key)||LaunchBoxService.Hash(pair.Key)!=pair.Value)throw new InvalidOperationException("LaunchBox data changed after preview: "+pair.Key);
        var sources=plan.ExternalDataInventory.ToDictionary(p=>p,p=>LaunchBoxService.ReadXml(p).ToString(SaveOptions.DisableFormatting),IgnoreCase);
        var items=selected.Where(Relevant).ToList();
        var blocked=FindUnsupported(plan,items,sources);
        if(blocked.Count>0)throw new InvalidOperationException(string.Join("; ",blocked.Values));
        var documents=BuildDocuments(plan,items,sources,out blocked);
        if(blocked.Count>0)throw new InvalidOperationException(string.Join("; ",blocked.Values));
        AssertInventory(plan);return documents;
    }
    private static bool Contains(string text,string id)=>id.Length>0&&text.Contains(id,StringComparison.OrdinalIgnoreCase);
    private static Dictionary<LaunchBoxPlanItem,string> FindUnsupported(LaunchBoxPlan plan,List<LaunchBoxPlanItem> items,Dictionary<string,string> sources)
    {
        var problems=new Dictionary<LaunchBoxPlanItem,string>();
        foreach(var (path,contents) in sources)
        {
            var affected=items.Where(i=>i.RemovedGameIds.Concat(i.RemovedApplicationIds).Any(id=>Contains(contents,id))).ToList();
            if(affected.Count==0)continue;
            XDocument document;
            try{document=LaunchBoxService.ReadXml(path);}catch(Exception ex)when(ex is IOException or System.Xml.XmlException)
            {foreach(var item in affected)problems[item]="A referenced data file cannot be parsed safely: "+path;continue;}
            foreach(var item in affected)
            {
                if(item.RemovedApplicationIds.Any(id=>Contains(contents,id)))
                {problems[item]="An external file references a removed launch option. Preserve it for review: "+path;continue;}
                foreach(var id in item.RemovedGameIds)
                {
                    if(!Contains(contents,id))continue;
                    var unsupported=!IsPlaylist(path,plan.SourcePath)||document.Root?.Name!="LaunchBox";
                    foreach(var node in document.DescendantNodes())
                    {
                        if(node is XText text&&Contains(text.Value,id))
                        {
                            var field=text.Parent;
                            if(field==null||!IgnoreCase.Equals(field.Name.LocalName,"GameId")||field.Parent?.Name!="PlaylistGame"||field.Parent.Parent!=document.Root||!IgnoreCase.Equals(field.Value,id))unsupported=true;
                        }
                        else if(node is XComment comment&&Contains(comment.Value,id)||node is XProcessingInstruction instruction&&Contains(instruction.Data,id))unsupported=true;
                    }
                    if(document.Descendants().Attributes().Any(a=>Contains(a.Value,id)))unsupported=true;
                    if(unsupported)problems[item]="An unsupported external reference needs review before removing this game ID: "+path;
                }
            }
        }
        return problems;
    }
    private static Dictionary<string,XDocument> BuildDocuments(LaunchBoxPlan plan,List<LaunchBoxPlanItem> items,Dictionary<string,string> sources,out Dictionary<LaunchBoxPlanItem,string> problems)
    {
        problems=new();var output=new Dictionary<string,XDocument>(IgnoreCase);
        var remaps=new Dictionary<string,string>(IgnoreCase);
        foreach(var item in items)foreach(var id in item.RemovedGameIds)
        {
            if(remaps.TryGetValue(id,out var target)&&target!=item.GameGuid){problems[item]="Two reviewed groups would remove the same game ID. Preview again.";continue;}
            remaps[id]=item.GameGuid;
        }
        var targets=items.Where(i=>i.GameGuid.Length>0).GroupBy(i=>i.GameGuid,IgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),IgnoreCase);
        foreach(var (path,contents) in sources.Where(s=>IsPlaylist(s.Key,plan.SourcePath)))
        {
            if(!remaps.Keys.Concat(targets.Keys).Any(id=>Contains(contents,id)))continue;
            var document=LaunchBoxService.ReadXml(path);if(document.Root?.Name!="LaunchBox")continue;
            var original=new XDocument(document);var oldIds=document.Root.Elements("PlaylistGame").ToDictionary(e=>e,e=>e.Elements().FirstOrDefault(f=>IgnoreCase.Equals(f.Name.LocalName,"GameId"))?.Value??"");
            foreach(var entry in document.Root.Elements("PlaylistGame"))
            {
                var field=entry.Elements().SingleOrDefault(e=>IgnoreCase.Equals(e.Name.LocalName,"GameId"));if(field==null)continue;
                if(remaps.TryGetValue(field.Value,out var mapped))field.Value=mapped;
                if(!targets.TryGetValue(field.Value,out var item))continue;
                var game=EffectiveGame(plan,item);if(game==null)continue;
                SetExisting(entry,"GameTitle",LaunchBoxService.Value(game,"Title"));
                SetExisting(entry,"GameFileName",Path.GetFileName(LaunchBoxService.Value(game,"ApplicationPath").Replace('\\','/')));
                SetExisting(entry,"GamePlatform",plan.PlatformName);
                var database=LaunchBoxService.Value(game,"DatabaseID");if(database.Length>0)SetExisting(entry,"LaunchBoxDbId",database);
            }
            foreach(var group in document.Root.Elements("PlaylistGame").GroupBy(e=>e.Elements().FirstOrDefault(f=>IgnoreCase.Equals(f.Name.LocalName,"GameId"))?.Value??"",IgnoreCase).Where(g=>g.Count()>1&&targets.ContainsKey(g.Key)).ToList())
            {
                // Prefer an existing membership of the surviving game, then its first playlist position.
                var keep=group.FirstOrDefault(e=>IgnoreCase.Equals(oldIds[e],group.Key))??group.First();
                foreach(var extra in group.Where(e=>e!=keep).ToList())
                {
                    if(!EquivalentMembership(keep,extra))
                    {problems[targets[group.Key]]="Duplicate playlist memberships contain conflicting custom fields. Review before combining: "+path;continue;}
                    extra.Remove();
                }
            }
            if(!XNode.DeepEquals(original,document))output[path]=document;
        }
        return output;
    }
    private static XElement? EffectiveGame(LaunchBoxPlan plan,LaunchBoxPlanItem item)
    {
        var source=plan.Document.Root!.Elements("Game").SingleOrDefault(e=>IgnoreCase.Equals(LaunchBoxService.Value(e,"ID"),item.GameGuid))??item.NewGame;
        if(source==null)return null;var result=new XElement(source);foreach(var pair in item.Changes)result.SetElementValue(pair.Key,pair.Value);return result;
    }
    private static void SetExisting(XElement parent,string name,string value){var field=parent.Element(name);if(field!=null)field.Value=value;}
    private static bool EquivalentMembership(XElement first,XElement second)
    {
        XElement Normalize(XElement element)
        {
            var clone=new XElement(element);foreach(var id in clone.Elements().Where(e=>IgnoreCase.Equals(e.Name.LocalName,"GameId")))id.Name="GameId";
            foreach(var field in clone.Elements().Where(e=>e.Name.LocalName is "ManualOrder" or "GameTitle" or "GameFileName").ToList())field.Remove();
            foreach(var space in clone.Nodes().OfType<XText>().Where(t=>t.GetType()==typeof(XText)&&string.IsNullOrWhiteSpace(t.Value)).ToList())space.Remove();
            return clone;
        }
        return XNode.DeepEquals(Normalize(first),Normalize(second));
    }
}
