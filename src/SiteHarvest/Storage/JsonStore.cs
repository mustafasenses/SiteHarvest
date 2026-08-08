using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using SiteHarvest.Models;

namespace SiteHarvest.Storage;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

public sealed class JsonStore
{
    public JsonStore(string dataRoot)
    {
        DataRoot = Path.GetFullPath(dataRoot);
        SitesDir = Path.Combine(DataRoot, "sites");
        AutomationsDir = Path.Combine(DataRoot, "automations");
        RunsDir = Path.Combine(DataRoot, "runs");
        SessionsDir = Path.Combine(DataRoot, "sessions");
        Directory.CreateDirectory(SitesDir);
        Directory.CreateDirectory(AutomationsDir);
        Directory.CreateDirectory(RunsDir);
        Directory.CreateDirectory(SessionsDir);
    }

    public string DataRoot { get; }
    public string SitesDir { get; }
    public string AutomationsDir { get; }
    public string RunsDir { get; }
    public string SessionsDir { get; }

    public string SessionPath(string siteId) =>
        Path.Combine(SessionsDir, $"{siteId}.json");

    public bool HasSession(string siteId) =>
        File.Exists(SessionPath(siteId));

    public void DeleteSession(string siteId)
    {
        var path = SessionPath(siteId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static JsonStore CreateDefault()
    {
        var env = Environment.GetEnvironmentVariable("SITE_HARVEST_DATA");
        if (!string.IsNullOrWhiteSpace(env))
            return new JsonStore(env);

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SiteHarvest.sln")))
                return new JsonStore(Path.Combine(dir.FullName, "data"));
            dir = dir.Parent;
        }

        var local = Path.Combine(Directory.GetCurrentDirectory(), "data");
        if (Directory.Exists(local))
            return new JsonStore(local);

        var home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".site-harvest",
            "data");
        return new JsonStore(home);
    }

    public static string NewId(string prefix) =>
        $"{prefix}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    public void SaveSite(SiteRecord site)
    {
        Write(Path.Combine(SitesDir, $"{site.Id}.json"), site);
    }

    public SiteRecord? GetSite(string idOrName)
    {
        var byId = Path.Combine(SitesDir, $"{idOrName}.json");
        if (File.Exists(byId))
            return Read<SiteRecord>(byId);

        return ListSites()
            .FirstOrDefault(s =>
                string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.Id, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public List<SiteRecord> ListSites() =>
        Directory.EnumerateFiles(SitesDir, "*.json")
            .Select(Read<SiteRecord>)
            .Where(s => s != null)
            .Cast<SiteRecord>()
            .OrderBy(s => s.Name)
            .ToList();

    public bool DeleteSite(string id)
    {
        var path = Path.Combine(SitesDir, $"{id}.json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public void SaveAutomation(AutomationRecord auto)
    {
        auto.UpdatedAt = DateTimeOffset.UtcNow;
        Write(Path.Combine(AutomationsDir, $"{auto.Id}.json"), auto);
    }

    public bool DeleteAutomation(string id)
    {
        var path = Path.Combine(AutomationsDir, $"{id}.json");
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    public bool DeleteRun(string runId)
    {
        var dir = RunDir(runId);
        if (!Directory.Exists(dir))
            return false;
        Directory.Delete(dir, recursive: true);
        return true;
    }

    public AutomationRecord? GetAutomation(string idOrName)
    {
        var byId = Path.Combine(AutomationsDir, $"{idOrName}.json");
        if (File.Exists(byId))
            return Read<AutomationRecord>(byId);

        return ListAutomations()
            .FirstOrDefault(a =>
                string.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Id, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public List<AutomationRecord> ListAutomations(string? siteId = null) =>
        Directory.EnumerateFiles(AutomationsDir, "*.json")
            .Select(Read<AutomationRecord>)
            .Where(a => a != null)
            .Cast<AutomationRecord>()
            .Where(a => siteId == null || a.SiteId == siteId)
            .OrderBy(a => a.Name)
            .ToList();

    public string RunDir(string runId) => Path.Combine(RunsDir, runId);

    public string RunMediaDir(string runId)
    {
        var dir = Path.Combine(RunDir(runId), "media");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void SaveRun(RunRecord run)
    {
        var dir = RunDir(run.Id);
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, "run.json"), run);
    }

    public void SaveItems(string runId, IReadOnlyList<HarvestItem> items)
    {
        var dir = RunDir(runId);
        Directory.CreateDirectory(dir);
        Write(Path.Combine(dir, "items.json"), items);
    }

    public RunRecord? GetRun(string runId)
    {
        var path = Path.Combine(RunDir(runId), "run.json");
        return File.Exists(path) ? Read<RunRecord>(path) : null;
    }

    public List<HarvestItem> GetItems(string runId)
    {
        var path = Path.Combine(RunDir(runId), "items.json");
        if (!File.Exists(path))
            return [];
        return Read<List<HarvestItem>>(path) ?? [];
    }

    public List<RunRecord> ListRuns(string? automationId = null) =>
        Directory.EnumerateDirectories(RunsDir)
            .Select(d => Path.Combine(d, "run.json"))
            .Where(File.Exists)
            .Select(Read<RunRecord>)
            .Where(r => r != null)
            .Cast<RunRecord>()
            .Where(r => automationId == null || r.AutomationId == automationId)
            .OrderByDescending(r => r.StartedAt)
            .ToList();

    private static void Write<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions.Default);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private static T? Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions.Default);
}
