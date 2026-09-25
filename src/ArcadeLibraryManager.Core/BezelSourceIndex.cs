using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArcadeLibraryManager.Core;

/// <summary>One bounded-directory index per preview. Only exact profile identities are retained.</summary>
internal sealed class BezelSourceIndex
{
    private readonly Dictionary<string, List<string>> mame = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> repository = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> mameIssues = new(StringComparer.OrdinalIgnoreCase);
    private string mameRoot = "", repositoryRoot = "";
    public string RepositoryIssue { get; private set; } = "";
    public IReadOnlyList<string> Mame(string id) => mame.GetValueOrDefault(id) ?? [];
    public IReadOnlyList<string> Repository(string id) => repository.GetValueOrDefault(id) ?? [];
    public string MameIssue(string id) => mameIssues.GetValueOrDefault(id, "");

    public string Fingerprint(string id)
    {
        var value = JsonSerializer.Serialize(new { mameRoot, repositoryRoot, Mame = Mame(id), Repository = Repository(id), MameIssue = MameIssue(id), RepositoryIssue });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public static BezelSourceIndex Create(string mamePath, string repositoryPath, IEnumerable<string> profileIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new BezelSourceIndex(); var ids = profileIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return result;
        foreach (var id in ids) { result.mame[id] = []; result.repository[id] = []; }
        if (!string.IsNullOrWhiteSpace(mamePath))
        {
            try
            {
                result.mameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mamePath));
                foreach (var id in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    foreach (var relative in new[] { id + ".png", id + ".zip", Path.Combine(id, "bezel.png"), Path.Combine(id, id + ".png") })
                    {
                        var candidate = SafeFiles.Child(result.mameRoot, relative);
                        try { if (PlainFile(candidate, result.mameRoot)) result.mame[id].Add(candidate); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.mameIssues[id] = "MAME artwork could not be fully checked: " + ex.Message; }
                    }
                    result.mame[id] = result.mame[id].Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { foreach (var id in ids) result.mameIssues[id] = "MAME artwork could not be checked: " + ex.Message; }
        }
        if (string.IsNullOrWhiteSpace(repositoryPath)) return result;
        try
        {
            result.repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            if (!Directory.Exists(result.repositoryRoot)) { result.RepositoryIssue = "The local bezel repository folder was not found: " + result.repositoryRoot; return result; }
            if (IgnoredDirectory(result.repositoryRoot)) { result.RepositoryIssue = "The local bezel repository must be a regular folder outside .git, not a linked directory."; return result; }
            var pending = new Stack<string>(); pending.Push(result.repositoryRoot);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested(); var directory = pending.Pop();
                try
                {
                    // A folder can be replaced by a link after it is queued; recheck before traversing it.
                    if (!PlainDirectory(directory, result.repositoryRoot)) continue;
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        ct.ThrowIfCancellationRequested();
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (!Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase)) pending.Push(path);
                            continue;
                        }
                        var extension = Path.GetExtension(path);
                        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                        var name = Path.GetFileNameWithoutExtension(path); var parentId = Path.GetFileName(directory);
                        var directMatch = ids.Contains(name);
                        var folderMatch = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) && name.Equals("bezel", StringComparison.OrdinalIgnoreCase) && ids.Contains(parentId);
                        if (!(directMatch || folderMatch) || !PlainFile(path, result.repositoryRoot)) continue;
                        if (directMatch) result.repository[name].Add(path);
                        if (folderMatch) result.repository[parentId].Add(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { result.RepositoryIssue = "The local bezel repository could not be fully indexed; resolve inaccessible folders before choosing an overlay. " + ex.Message; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { result.RepositoryIssue = "The local bezel repository could not be indexed: " + ex.Message; }
        foreach (var id in ids) result.repository[id] = result.repository[id].Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    private static bool IgnoredDirectory(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals(".git", StringComparison.OrdinalIgnoreCase)
        || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PlainDirectory(string path, string root)
    {
        string? current = path;
        while (current != null)
        {
            if (IgnoredDirectory(current)) return false;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static bool PlainFile(string path, string root)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var parent = Path.GetDirectoryName(path);
        return parent != null && PlainDirectory(parent, root);
    }
}
