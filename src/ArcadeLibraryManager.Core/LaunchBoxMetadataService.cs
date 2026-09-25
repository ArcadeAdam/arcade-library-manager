using Microsoft.Data.Sqlite;
using System.Globalization;

namespace ArcadeLibraryManager.Core;

public sealed record LaunchBoxMetadataMatch(int DatabaseId, string Name, Dictionary<string, string> Fields);

/// <summary>Reads local metadata without changing LaunchBox's database or its journal.</summary>
public sealed class LaunchBoxMetadataCatalog : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly HashSet<string> gameColumns;
    private readonly bool hasAliases;
    private Dictionary<string, HashSet<int>>? exactNames;

    internal LaunchBoxMetadataCatalog(string path)
    {
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            gameColumns = Columns("Games");
            if (!new[] { "DatabaseID", "Name", "Platform" }.All(gameColumns.Contains))
                throw new InvalidDataException("LaunchBox metadata has an unsupported Games schema.");
            hasAliases = new[] { "DatabaseID", "AlternateName" }.All(Columns("GameAlternateTitles").Contains);
        }
        catch { connection.Dispose(); throw; }
    }

    private HashSet<string> Columns(string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    public LaunchBoxMetadataMatch? FindExact(string name, int? databaseId = null)
    {
        if (databaseId is not > 0)
        {
            if (exactNames is null)
            {
                exactNames = new(StringComparer.OrdinalIgnoreCase);
                using var indexCommand = connection.CreateCommand();
                indexCommand.CommandText = "SELECT DatabaseID,Name FROM Games WHERE Platform='Arcade'" +
                    (hasAliases ? " UNION ALL SELECT g.DatabaseID,a.AlternateName FROM Games g JOIN GameAlternateTitles a ON g.DatabaseID=a.DatabaseID WHERE g.Platform='Arcade'" : "");
                using var indexReader = indexCommand.ExecuteReader();
                while (indexReader.Read())
                {
                    if (indexReader.IsDBNull(0) || indexReader.IsDBNull(1)) continue;
                    var key = indexReader.GetString(1).Trim();
                    if (key.Length == 0) continue;
                    if (!exactNames.TryGetValue(key, out var values)) exactNames[key] = values = [];
                    values.Add(indexReader.GetInt32(0));
                }
            }
            if (!exactNames.TryGetValue(name.Trim(), out var ids) || ids.Count != 1) return null;
            databaseId = ids.First();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Games WHERE DatabaseID=$id LIMIT 2";
        command.Parameters.AddWithValue("$id", databaseId.Value);
        using var reader = command.ExecuteReader();
        LaunchBoxMetadataMatch? match = null;
        while (reader.Read())
        {
            if (match is not null) return null;
            string Get(string field) => gameColumns.Contains(field) && !reader.IsDBNull(reader.GetOrdinal(field))
                ? Convert.ToString(reader[field], CultureInfo.InvariantCulture) ?? "" : "";
            var fields = new Dictionary<string, string>();
            foreach (var (source, target) in new[]
            {
                ("Overview", "Notes"), ("Developer", "Developer"), ("Publisher", "Publisher"),
                ("Genres", "Genre"), ("ReleaseDate", "ReleaseDate"), ("MaxPlayers", "MaxPlayers"),
                ("WikipediaURL", "WikipediaURL"), ("ESRB", "Rating")
            })
            {
                var value = Get(source);
                if (!string.IsNullOrWhiteSpace(value)) fields[target] = value;
            }
            if (!fields.ContainsKey("ReleaseDate") && int.TryParse(Get("ReleaseYear"), out var year) && year is >= 1000 and <= 9999)
                fields["ReleaseDate"] = $"{year:0000}-01-01";
            match = new(reader.GetInt32(reader.GetOrdinal("DatabaseID")), Get("Name"), fields);
        }
        return match;
    }

    public void Dispose() => connection.Dispose();
}

/// <summary>Opens existing local LaunchBox metadata. It never downloads, imports, or writes metadata or artwork.</summary>
public static class LaunchBoxMetadataService
{
    internal static string Cache(AppSettings settings) => Path.Combine(
        string.IsNullOrWhiteSpace(settings.CachePath) ? Path.Combine(AppContext.BaseDirectory, "data", "cache") : settings.CachePath,
        "launchbox");

    public static LaunchBoxMetadataCatalog? TryOpen(AppSettings settings)
    {
        // Preserve access to metadata cached by earlier versions, without modifying or refreshing it.
        var candidates = new List<string> { Path.Combine(Cache(settings), "metadata.db") };
        if (!string.IsNullOrWhiteSpace(settings.LaunchBoxPath))
            candidates.Add(Path.Combine(settings.LaunchBoxPath, "Metadata", "LaunchBox.Metadata.db"));
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try { return new(path); }
            catch (Exception ex) when (ex is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
