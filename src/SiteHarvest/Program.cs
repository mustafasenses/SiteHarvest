using SiteHarvest.Services;
using SiteHarvest.Storage;

var dataRoot = ParseDataRoot(args);
var store = string.IsNullOrWhiteSpace(dataRoot)
    ? JsonStore.CreateDefault()
    : new JsonStore(dataRoot);

await new MenuService(store).RunAsync();
return 0;

static string? ParseDataRoot(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "-d" or "--data" && i + 1 < args.Length)
            return args[i + 1];
        if (args[i].StartsWith("--data=", StringComparison.Ordinal))
            return args[i]["--data=".Length..];
    }

    return null;
}
