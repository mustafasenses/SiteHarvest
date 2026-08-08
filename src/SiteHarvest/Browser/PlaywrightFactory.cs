using Microsoft.Playwright;

namespace SiteHarvest.Browser;

public static class PlaywrightFactory
{
    public static BrowserTypeLaunchOptions LaunchOptions(bool headless) => new()
    {
        Headless = headless,
        Args =
        [
            "--disable-dev-shm-usage",
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-gpu",
        ],
    };

    public static BrowserNewContextOptions ContextOptions(string? storageStatePath = null)
    {
        var opts = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
        };
        if (!string.IsNullOrWhiteSpace(storageStatePath) && File.Exists(storageStatePath))
            opts.StorageStatePath = storageStatePath;
        return opts;
    }

    public static BrowserNewPageOptions PageOptions() => new()
    {
        ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
    };
}
