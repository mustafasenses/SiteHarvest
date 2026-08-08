using SiteHarvest.Models;
using SiteHarvest.Storage;
using SiteHarvest.Ui;
using Spectre.Console;

namespace SiteHarvest.Services;

public sealed class MenuService
{
    private readonly JsonStore _store;

    private enum Action
    {
        ListSites,
        AddSite,
        RemoveSite,
        ListAutos,
        AddAuto,
        Teach,
        Run,
        ListRuns,
        Export,
        Browser,
        Exit,
    }

    private sealed record MenuItem(Action Action, string Label, string Hint);

    private sealed record PickOption<T>(T? Value, string Label, bool IsCancel = false)
    {
        public override string ToString() => Label;
    }

    public MenuService(JsonStore store) => _store = store;

    public async Task RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Term.Header(_store.DataRoot);

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                Term.SelectPrompt<MenuItem>("What do you want to do?")
                    .AddChoices(
                        new MenuItem(Action.ListSites, "List sites", "saved sites"),
                        new MenuItem(Action.AddSite, "Add site", "new site"),
                        new MenuItem(Action.RemoveSite, "Remove site", "delete"),
                        new MenuItem(Action.ListAutos, "List automations", "fields / steps"),
                        new MenuItem(Action.AddAuto, "Add automation", "site + start URL"),
                        new MenuItem(Action.Teach, "Teach", "mark steps in browser"),
                        new MenuItem(Action.Run, "Run harvest", "start scrape"),
                        new MenuItem(Action.ListRuns, "List runs", "history"),
                        new MenuItem(Action.Export, "Export (zip)", "package a run"),
                        new MenuItem(Action.Browser, "Install browser", "download Chromium"),
                        new MenuItem(Action.Exit, "Exit", "quit"))
                    .UseConverter(m =>
                        m.Action == Action.Exit
                            ? $"[{Term.Muted}]{m.Label}[/]"
                            : $"[{Term.Title}]{m.Label}[/]  [{Term.Muted}]{m.Hint}[/]"));

            AnsiConsole.WriteLine();

            try
            {
                switch (choice.Action)
                {
                    case Action.ListSites:
                        ListSites();
                        Term.Pause();
                        break;
                    case Action.AddSite:
                        AddSite();
                        Term.Pause();
                        break;
                    case Action.RemoveSite:
                        RemoveSite();
                        Term.Pause();
                        break;
                    case Action.ListAutos:
                        ListAutomations();
                        Term.Pause();
                        break;
                    case Action.AddAuto:
                        AddAutomation();
                        Term.Pause();
                        break;
                    case Action.Teach:
                        await TeachAsync();
                        Term.Pause();
                        break;
                    case Action.Run:
                        await RunHarvestAsync();
                        Term.Pause();
                        break;
                    case Action.ListRuns:
                        ListRuns();
                        Term.Pause();
                        break;
                    case Action.Export:
                        Export();
                        Term.Pause();
                        break;
                    case Action.Browser:
                        InstallBrowser();
                        Term.Pause();
                        break;
                    case Action.Exit:
                        AnsiConsole.MarkupLine($"[{Term.Muted}]Bye.[/]");
                        return;
                }
            }
            catch (Exception ex)
            {
                Term.Error(ex.Message);
                Term.Pause();
            }
        }
    }

    private void ListSites()
    {
        var sites = _store.ListSites();
        if (sites.Count == 0)
        {
            Term.WarnMsg("No sites yet.");
            Term.Hint("Choose Add site from the menu.");
            return;
        }

        var table = Term.NewTable();
        table.AddColumn(new TableColumn("[grey]#[/]").Centered());
        table.AddColumn($"[{Term.Accent}]Site[/]");
        table.AddColumn($"[{Term.Muted}]Base URL[/]");

        for (var i = 0; i < sites.Count; i++)
        {
            var s = sites[i];
            table.AddRow(
                $"{i + 1}",
                $"[bold]{Term.Escape(s.Name)}[/]",
                $"[{Term.Muted}]{Term.Escape(s.BaseUrl ?? "—")}[/]");
        }

        AnsiConsole.Write(table);
        Term.Hint($"{sites.Count} site(s)");
    }

    private void AddSite()
    {
        Term.Rule("New site");
        var name = Term.AskText("Site name");
        var url = Term.AskText("Base URL (optional)", optional: true);

        if (!string.IsNullOrWhiteSpace(url)
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            Term.WarnMsg("URL looks invalid; saving anyway.");
        }

        var site = new SiteRecord
        {
            Id = JsonStore.NewId("site"),
            Name = name,
            BaseUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
        };
        _store.SaveSite(site);
        Term.Success($"Site added: {site.Name}");
        Term.Hint("Next: Add automation → Teach → Run harvest");
    }

    private void RemoveSite()
    {
        var site = PickSite("Select site to remove");
        if (site is null)
            return;

        var autos = _store.ListAutomations(site.Id);
        if (autos.Count > 0)
        {
            Term.WarnMsg($"This site has {autos.Count} automation(s):");
            foreach (var a in autos)
                AnsiConsole.MarkupLine($"  [{Term.Muted}]•[/] {Term.Escape(a.Name)}");

            if (!Term.Confirm("Also delete automations and runs?", defaultValue: false))
            {
                Term.Hint("Cancelled.");
                return;
            }

            foreach (var a in autos)
            {
                foreach (var r in _store.ListRuns(a.Id))
                    _store.DeleteRun(r.Id);
                _store.DeleteAutomation(a.Id);
            }
        }
        else if (!Term.Confirm($"Delete \"{site.Name}\"?", defaultValue: false))
        {
            Term.Hint("Cancelled.");
            return;
        }

        _store.DeleteSite(site.Id);
        Term.Success($"Site removed: {site.Name}");
    }

    private void ListAutomations()
    {
        var list = _store.ListAutomations();
        if (list.Count == 0)
        {
            Term.WarnMsg("No automations yet.");
            Term.Hint("Choose Add automation from the menu.");
            return;
        }

        var sites = _store.ListSites().ToDictionary(s => s.Id, s => s.Name);
        var table = Term.NewTable();
        table.AddColumn(new TableColumn("[grey]#[/]").Centered());
        table.AddColumn($"[{Term.Accent}]Automation[/]");
        table.AddColumn($"[{Term.Muted}]Site[/]");
        table.AddColumn(new TableColumn("Fields").Centered());
        table.AddColumn(new TableColumn("Steps").Centered());
        table.AddColumn($"[{Term.Muted}]Start[/]");

        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var siteName = sites.GetValueOrDefault(a.SiteId, a.SiteId);
            table.AddRow(
                $"{i + 1}",
                $"[bold]{Term.Escape(a.Name)}[/]",
                Term.Escape(siteName),
                a.Fields.Count.ToString(),
                a.Steps.Count.ToString(),
                $"[{Term.Muted}]{Term.Escape(Truncate(a.StartUrl, 40))}[/]");
        }

        AnsiConsole.Write(table);
    }

    private void AddAutomation()
    {
        Term.Rule("New automation");
        var site = PickSite("Which site?");
        if (site is null)
            return;

        var name = Term.AskText("Automation name");
        while (true)
        {
            var start = Term.AskText("Start URL (https://...)");
            if (Uri.TryCreate(start, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var auto = new AutomationRecord
                {
                    Id = JsonStore.NewId("auto"),
                    SiteId = site.Id,
                    Name = name,
                    StartUrl = uri.ToString(),
                };
                _store.SaveAutomation(auto);
                Term.Success($"Automation added: {auto.Name}");
                Term.Hint("Next: Teach to mark fields in the browser.");
                return;
            }

            Term.WarnMsg("Enter a valid http(s) URL.");
        }
    }

    private async Task TeachAsync()
    {
        var auto = PickAutomation("Select automation to teach");
        if (auto is null)
            return;

        Term.InfoMsg("Opening browser…");
        await new TeachService(_store).RunAsync(auto.Id);
        Term.Success("Teach session finished.");
    }

    private async Task RunHarvestAsync()
    {
        var auto = PickAutomation("Select automation to run");
        if (auto is null)
            return;

        Term.Rule("Harvest settings");
        AnsiConsole.MarkupLine(
            $"[{Term.Muted}]Max items:[/] enter a number to stop early  ·  " +
            $"[{Term.Muted}]leave empty[/] → run until the end");

        var maxStr = Term.AskText("Max items (empty = no limit)", optional: true);
        int? max = null;
        if (int.TryParse(maxStr, out var m) && m > 0)
            max = m;
        else if (!string.IsNullOrEmpty(maxStr))
            Term.WarnMsg("Invalid number; treating as no limit.");

        var headed = Term.Confirm("Show the browser window?", defaultValue: false);

        var summary = new Panel(
                new Markup(
                    $"[bold]{Term.Escape(auto.Name)}[/]\n" +
                    $"[{Term.Muted}]limit[/]  {(max is > 0 ? $"[gold3]{max}[/]" : $"[{Term.Ok}]none[/]")}\n" +
                    $"[{Term.Muted}]mode[/]   {(headed ? "headed" : "headless")}"))
            .Header($"[{Term.Accent}]Summary[/]", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.DarkCyan);

        AnsiConsole.Write(summary);

        if (!Term.Confirm("Start harvest?", defaultValue: true))
        {
            Term.Hint("Cancelled.");
            return;
        }

        Term.InfoMsg(max is > 0
            ? $"Starting harvest (max {max})…"
            : "Starting harvest (no limit)…");

        var run = await new HarvestService(_store).RunAsync(auto.Id, max, headless: !headed);

        var color = Term.StatusColor(run.Status);
        AnsiConsole.MarkupLine(
            $"[{Term.Ok}]✓[/] Done  [{Term.Muted}]items[/] [bold]{run.ItemCount}[/]  " +
            $"[{Term.Muted}]status[/] [{color}]{Term.Escape(run.Status)}[/]  " +
            $"[{Term.Muted}]run[/] {Term.Escape(run.Id)}");
    }

    private void ListRuns()
    {
        var runs = _store.ListRuns();
        if (runs.Count == 0)
        {
            Term.WarnMsg("No runs yet.");
            return;
        }

        var table = Term.NewTable();
        table.AddColumn(new TableColumn("[grey]#[/]").Centered());
        table.AddColumn("Status");
        table.AddColumn(new TableColumn("Items").RightAligned());
        table.AddColumn($"[{Term.Muted}]Started[/]");
        table.AddColumn($"[{Term.Muted}]Id[/]");

        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            var c = Term.StatusColor(r.Status);
            table.AddRow(
                $"{i + 1}",
                $"[{c}]{Term.Escape(r.Status)}[/]",
                r.ItemCount.ToString(),
                $"[{Term.Muted}]{r.StartedAt:yyyy-MM-dd HH:mm}[/]",
                $"[{Term.Muted}]{Term.Escape(Truncate(r.Id, 28))}[/]");
        }

        AnsiConsole.Write(table);
    }

    private void Export()
    {
        var run = PickRun("Select run to export");
        if (run is null)
            return;

        var path = new ExportService(_store).Export(run.Id);
        Term.Success($"Zip ready: {path}");
    }

    private static void InstallBrowser()
    {
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Line)
            .SpinnerStyle(Style.Parse(Term.Accent))
            .Start("Installing Playwright Chromium…", _ =>
            {
                var code = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (code != 0)
                    throw new InvalidOperationException($"Playwright install exit code: {code}");
            });
        Term.Success("Chromium is ready.");
    }

    private SiteRecord? PickSite(string prompt)
    {
        var sites = _store.ListSites();
        if (sites.Count == 0)
        {
            Term.WarnMsg("Add a site first.");
            return null;
        }

        var options = sites
            .Select(s => new PickOption<SiteRecord>(
                s,
                $"[bold]{Term.Escape(s.Name)}[/]  [{Term.Muted}]{Term.Escape(s.BaseUrl ?? "—")}[/]"))
            .Append(new PickOption<SiteRecord>(null, $"[{Term.Muted}]Cancel[/]", IsCancel: true))
            .ToList();

        var picked = AnsiConsole.Prompt(
            Term.SelectPrompt<PickOption<SiteRecord>>(prompt)
                .AddChoices(options)
                .UseConverter(o => o.Label));

        return picked.IsCancel ? null : picked.Value;
    }

    private AutomationRecord? PickAutomation(string prompt)
    {
        var list = _store.ListAutomations();
        if (list.Count == 0)
        {
            Term.WarnMsg("Add an automation first.");
            return null;
        }

        var sites = _store.ListSites().ToDictionary(s => s.Id, s => s.Name);
        var options = list
            .Select(a =>
            {
                var siteName = sites.GetValueOrDefault(a.SiteId, "?");
                return new PickOption<AutomationRecord>(
                    a,
                    $"[bold]{Term.Escape(a.Name)}[/]  [{Term.Muted}]{Term.Escape(siteName)} · fields={a.Fields.Count}[/]");
            })
            .Append(new PickOption<AutomationRecord>(null, $"[{Term.Muted}]Cancel[/]", IsCancel: true))
            .ToList();

        var picked = AnsiConsole.Prompt(
            Term.SelectPrompt<PickOption<AutomationRecord>>(prompt)
                .AddChoices(options)
                .UseConverter(o => o.Label));

        return picked.IsCancel ? null : picked.Value;
    }

    private RunRecord? PickRun(string prompt)
    {
        var runs = _store.ListRuns().Take(20).ToList();
        if (runs.Count == 0)
        {
            Term.WarnMsg("No runs yet.");
            return null;
        }

        var options = runs
            .Select(r =>
            {
                var c = Term.StatusColor(r.Status);
                return new PickOption<RunRecord>(
                    r,
                    $"[{c}]{r.Status}[/]  items={r.ItemCount}  [{Term.Muted}]{Term.Escape(Truncate(r.Id, 24))}[/]");
            })
            .Append(new PickOption<RunRecord>(null, $"[{Term.Muted}]Cancel[/]", IsCancel: true))
            .ToList();

        var picked = AnsiConsole.Prompt(
            Term.SelectPrompt<PickOption<RunRecord>>(prompt)
                .AddChoices(options)
                .UseConverter(o => o.Label));

        return picked.IsCancel ? null : picked.Value;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
