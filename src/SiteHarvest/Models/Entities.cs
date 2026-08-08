namespace SiteHarvest.Models;

public static class FieldTypes
{
    public const string Text = "text";
    public const string Image = "image";
    public const string Url = "url";

    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        Text,
        Image,
        Url,
    };

    public static string Normalize(string? type)
    {
        var t = (type ?? Text).Trim().ToLowerInvariant();
        if (!Allowed.Contains(t))
            throw new ArgumentException($"Invalid type: {type}. Allowed: text, image, url");
        return t;
    }
}

public class SiteRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AutomationRecord
{
    public string Id { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int SchemaVersion { get; set; } = 1;
    public string StartUrl { get; set; } = string.Empty;
    public List<RecordedStep> Steps { get; set; } = [];
    public List<FieldMapping> Fields { get; set; } = [];
    public bool HasPagination { get; set; }
    public string? NextPageSelector { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class RecordedStep
{
    public string Type { get; set; } = "click";
    public string Selector { get; set; } = string.Empty;
    public string? UrlAfter { get; set; }
    public bool ListBranch { get; set; }
}

public class FieldMapping
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = FieldTypes.Text;
    public string Selector { get; set; } = string.Empty;
}

public class RunRecord
{
    public string Id { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int FoundCount { get; set; }
    public int ItemCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StartUrl { get; set; }
}

public class HarvestItem
{
    public string ExternalKey { get; set; } = string.Empty;
    public string? PageUrl { get; set; }
    public int Index { get; set; }
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ExportManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Tool { get; set; } = "site-harvest";
    public string RunId { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string? SiteName { get; set; }
    public string? AutomationName { get; set; }
    public string? StartUrl { get; set; }
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ItemCount { get; set; }
}
