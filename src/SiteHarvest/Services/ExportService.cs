using System.IO.Compression;
using System.Text.Json;
using SiteHarvest.Models;
using SiteHarvest.Storage;

namespace SiteHarvest.Services;

public sealed class ExportService
{
    private readonly JsonStore _store;

    public ExportService(JsonStore store) => _store = store;

    public string Export(string runId, string? outputPath = null)
    {
        var run = _store.GetRun(runId)
                  ?? throw new InvalidOperationException($"Run not found: {runId}");
        var items = _store.GetItems(runId);
        var auto = _store.GetAutomation(run.AutomationId);
        var site = _store.GetSite(run.SiteId);

        var manifest = new ExportManifest
        {
            SchemaVersion = 1,
            RunId = run.Id,
            AutomationId = run.AutomationId,
            SiteId = run.SiteId,
            SiteName = site?.Name,
            AutomationName = auto?.Name,
            StartUrl = run.StartUrl ?? auto?.StartUrl,
            ExportedAt = DateTimeOffset.UtcNow,
            ItemCount = items.Count,
        };

        var exportDir = Path.Combine(_store.DataRoot, "exports");
        Directory.CreateDirectory(exportDir);

        var zipPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(exportDir, $"{run.Id}.zip")
            : Path.GetFullPath(outputPath);

        var zipDir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrWhiteSpace(zipDir))
            Directory.CreateDirectory(zipDir);

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        var runDir = _store.RunDir(runId);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            AddJson(zip, "manifest.json", manifest);
            AddJson(zip, "items.json", items);
            AddJson(zip, "run.json", run);
            if (auto != null)
                AddJson(zip, "automation.json", auto);

            var mediaDir = Path.Combine(runDir, "media");
            if (Directory.Exists(mediaDir))
            {
                foreach (var file in Directory.EnumerateFiles(mediaDir))
                {
                    var entryName = Path.Combine("media", Path.GetFileName(file)).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }
        }

        Console.WriteLine($"Export: {zipPath} ({items.Count} item)");
        return zipPath;
    }

    private static void AddJson<T>(ZipArchive zip, string entryName, T value)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        JsonSerializer.Serialize(writer, value, JsonOptions.Default);
    }
}
