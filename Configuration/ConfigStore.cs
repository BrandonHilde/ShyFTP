using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShyFtp.Configuration;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Path { get; }

    public ConfigStore(string path)
    {
        Path = path;
    }

    public static string ResolveDefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SHYFTP_CONFIG");
        if (!string.IsNullOrWhiteSpace(env))
            return env!;

        var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(dir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = System.IO.Path.Combine(home, ".config");
        }

        return System.IO.Path.Combine(dir, "ShyFTP", "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(Path))
        {
            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        var json = File.ReadAllText(Path);
        if (string.IsNullOrWhiteSpace(json))
            return new AppConfig();

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            config.Servers = new Dictionary<string, ServerProfile>(config.Servers, StringComparer.OrdinalIgnoreCase);
            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Config file '{Path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Save(AppConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(Path, json);
    }

    public FolderProfile? FindFolder(AppConfig config, string localPath)
    {
        var target = Normalize(localPath);
        FolderProfile? best = null;
        var bestLength = -1;

        foreach (var folder in config.Folders)
        {
            var candidate = Normalize(folder.Path);
            var isExact = string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase);
            var isParent = target.StartsWith(candidate + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if ((isExact || isParent) && candidate.Length > bestLength)
            {
                best = folder;
                bestLength = candidate.Length;
            }
        }

        return best;
    }

    private static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}