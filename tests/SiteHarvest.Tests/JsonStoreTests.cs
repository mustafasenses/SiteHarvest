using System.Text.Json;
using SiteHarvest.Models;
using SiteHarvest.Storage;

namespace SiteHarvest.Tests;

public class JsonStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JsonStore _store;

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "site-harvest-tests", Guid.NewGuid().ToString("N"));
        _store = new JsonStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Save_and_get_site_by_id_and_name()
    {
        var site = new SiteRecord
        {
            Id = "site_test_1",
            Name = "Demo Factory",
            BaseUrl = "https://example.com",
        };
        _store.SaveSite(site);

        Assert.Equal("Demo Factory", _store.GetSite("site_test_1")!.Name);
        Assert.Equal("site_test_1", _store.GetSite("demo factory")!.Id);
        Assert.Single(_store.ListSites());
    }

    [Fact]
    public void Delete_site_and_cascade_helpers()
    {
        var site = new SiteRecord { Id = "site_del", Name = "Gone" };
        _store.SaveSite(site);

        var auto = new AutomationRecord
        {
            Id = "auto_del",
            SiteId = site.Id,
            Name = "products",
            StartUrl = "https://example.com/list",
        };
        _store.SaveAutomation(auto);

        var run = new RunRecord
        {
            Id = "run_del",
            AutomationId = auto.Id,
            SiteId = site.Id,
            Status = "succeeded",
            ItemCount = 0,
        };
        _store.SaveRun(run);
        _store.SaveItems(run.Id, []);

        Assert.True(_store.DeleteRun(run.Id));
        Assert.True(_store.DeleteAutomation(auto.Id));
        Assert.True(_store.DeleteSite(site.Id));
        Assert.Empty(_store.ListSites());
        Assert.Empty(_store.ListAutomations());
        Assert.Empty(_store.ListRuns());
    }

    [Fact]
    public void ListAutomations_filters_by_site()
    {
        _store.SaveSite(new SiteRecord { Id = "s1", Name = "A" });
        _store.SaveSite(new SiteRecord { Id = "s2", Name = "B" });
        _store.SaveAutomation(new AutomationRecord
        {
            Id = "a1", SiteId = "s1", Name = "one", StartUrl = "https://a.test",
        });
        _store.SaveAutomation(new AutomationRecord
        {
            Id = "a2", SiteId = "s2", Name = "two", StartUrl = "https://b.test",
        });

        Assert.Single(_store.ListAutomations("s1"));
        Assert.Equal(2, _store.ListAutomations().Count);
    }

    [Fact]
    public void Json_writes_turkish_characters_unescaped()
    {
        var site = new SiteRecord
        {
            Id = "site_tr",
            Name = "İzmir Seramik — RÖLYEFLİ",
        };
        _store.SaveSite(site);

        var raw = File.ReadAllText(Path.Combine(_store.SitesDir, "site_tr.json"));
        Assert.Contains("İzmir", raw);
        Assert.Contains("RÖLYEFLİ", raw);
        Assert.DoesNotContain("\\u0130", raw);

        var roundTrip = JsonSerializer.Deserialize<SiteRecord>(raw, JsonOptions.Default);
        Assert.Equal(site.Name, roundTrip!.Name);
    }
}
