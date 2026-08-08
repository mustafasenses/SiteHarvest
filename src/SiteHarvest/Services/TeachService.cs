using System.Text.Json;
using Microsoft.Playwright;
using SiteHarvest.Browser;
using SiteHarvest.Helpers;
using SiteHarvest.Models;
using SiteHarvest.Storage;
using SiteHarvest.Ui;
using Spectre.Console;

namespace SiteHarvest.Services;

public sealed class TeachService
{
    private readonly JsonStore _store;

    private enum MenuAction
    {
        AddClick,
        AddField,
        MarkNextPage,
        ClearNextPage,
        ToggleRepeat,
        UndoStep,
        UndoField,
        Status,
        Save,
        Finish,
    }

    private sealed record MenuItem(MenuAction Action, string Label, string Hint);

    public TeachService(JsonStore store) => _store = store;

    public async Task RunAsync(string automationIdOrName, CancellationToken ct = default)
    {
        var auto = _store.GetAutomation(automationIdOrName)
                   ?? throw new InvalidOperationException($"Automation not found: {automationIdOrName}");

        AnsiConsole.Write(new Panel(
                new Markup(
                    $"[bold]{Term.Escape(auto.Name)}[/]\n" +
                    $"[{Term.Muted}]{Term.Escape(auto.Id)}[/]\n" +
                    $"[{Term.AccentDim}]{Term.Escape(auto.StartUrl)}[/]"))
            .Header($"[{Term.Accent}]Teach[/]", Justify.Left)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.DarkCyan));

        Term.InfoMsg("Opening browser…");
        Term.Hint("Use the menu to add clicks and fields. Say Yes if a click should run for every list item.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            PlaywrightFactory.LaunchOptions(headless: false));
        var page = await browser.NewPageAsync(PlaywrightFactory.PageOptions());

        var pickLock = new object();
        string? pendingMode = null;
        TaskCompletionSource<PickEvent>? pendingTcs = null;

        await page.ExposeBindingAsync(
            "siteHarvestOnPick",
            (BindingSource _, string json) =>
            {
                PickEvent? ev;
                try
                {
                    ev = JsonSerializer.Deserialize<PickEvent>(json, JsonOptions.Default);
                }
                catch
                {
                    return;
                }

                if (ev == null || string.IsNullOrWhiteSpace(ev.Selector))
                    return;

                lock (pickLock)
                {
                    if (pendingTcs == null || pendingMode == null)
                        return;
                    if (!string.Equals(ev.Mode, pendingMode, StringComparison.OrdinalIgnoreCase))
                        return;

                    pendingTcs.TrySetResult(ev);
                    pendingTcs = null;
                    pendingMode = null;
                }
            });

        await page.GotoAsync(auto.StartUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 60_000,
        });
        await InjectPickerAsync(page, "idle");

        page.FrameNavigated += async (_, frame) =>
        {
            if (frame != page.MainFrame) return;
            try { await InjectPickerAsync(page, "idle"); }
            catch { }
        };

        var dirty = false;
        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();
            PrintHud(auto, dirty);

            var choice = AnsiConsole.Prompt(
                Term.SelectPrompt<MenuItem>("What do you want to do?")
                    .AddChoices(BuildMenu(auto))
                    .UseConverter(m =>
                        $"[{Term.Title}]{m.Label}[/]" +
                        (string.IsNullOrEmpty(m.Hint) ? "" : $"  [{Term.Muted}]{m.Hint}[/]")));

            try
            {
                switch (choice.Action)
                {
                    case MenuAction.AddClick:
                        dirty |= await AddClickAsync(auto, page, WaitPickAsync, ct);
                        break;

                    case MenuAction.AddField:
                        dirty |= await AddFieldAsync(auto, page, WaitPickAsync, ct);
                        break;

                    case MenuAction.MarkNextPage:
                        dirty |= await MarkNextPageAsync(auto, page, WaitPickAsync, ct);
                        break;

                    case MenuAction.ClearNextPage:
                        auto.HasPagination = false;
                        auto.NextPageSelector = null;
                        dirty = true;
                        Term.Success("Pagination cleared.");
                        break;

                    case MenuAction.ToggleRepeat:
                        if (auto.Steps.Count == 0)
                        {
                            Term.WarnMsg("Add a click first.");
                            break;
                        }

                        var last = auto.Steps[^1];
                        last.ListBranch = !last.ListBranch;
                        if (last.ListBranch)
                        {
                            foreach (var s in auto.Steps.Where(s => s != last && s.ListBranch))
                                s.ListBranch = false;
                        }

                        dirty = true;
                        Term.Success(
                            last.ListBranch
                                ? "Last click: will repeat for every list item."
                                : "Last click: once only.");
                        break;

                    case MenuAction.UndoStep:
                        if (auto.Steps.Count == 0)
                        {
                            Term.WarnMsg("No step to undo.");
                            break;
                        }

                        var removedStep = auto.Steps[^1];
                        auto.Steps.RemoveAt(auto.Steps.Count - 1);
                        dirty = true;
                        Term.Success($"Step removed: {Truncate(removedStep.Selector, 60)}");
                        break;

                    case MenuAction.UndoField:
                        if (auto.Fields.Count == 0)
                        {
                            Term.WarnMsg("No field to undo.");
                            break;
                        }

                        var removedField = auto.Fields[^1];
                        auto.Fields.RemoveAt(auto.Fields.Count - 1);
                        dirty = true;
                        Term.Success($"Field removed: {removedField.Key}");
                        break;

                    case MenuAction.Status:
                        PrintStatus(auto);
                        break;

                    case MenuAction.Save:
                        _store.SaveAutomation(auto);
                        dirty = false;
                        Term.Success("Saved.");
                        break;

                    case MenuAction.Finish:
                        if (dirty && Term.Confirm("You have unsaved changes. Save?", defaultValue: true))
                        {
                            _store.SaveAutomation(auto);
                            Term.Success("Saved.");
                        }

                        return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                Term.WarnMsg($"Timed out: {ex.Message}");
                try { await InjectPickerAsync(page, "idle"); } catch { }
            }
            catch (Exception ex)
            {
                Term.Error(ex.Message);
                try { await InjectPickerAsync(page, "idle"); } catch { }
            }
        }

        async Task<PickEvent> WaitPickAsync(string mode, TimeSpan timeout, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<PickEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pickLock)
            {
                pendingMode = mode;
                pendingTcs = tcs;
            }

            using var reg = token.Register(() => tcs.TrySetCanceled(token));
            var delay = Task.Delay(timeout, token);
            var completed = await Task.WhenAny(tcs.Task, delay);
            if (completed != tcs.Task)
            {
                lock (pickLock)
                {
                    if (pendingTcs == tcs)
                    {
                        pendingTcs = null;
                        pendingMode = null;
                    }
                }

                throw new TimeoutException("Timed out waiting for a browser click");
            }

            return await tcs.Task;
        }
    }

    private static void PrintHud(AutomationRecord auto, bool dirty)
    {
        var repeat = auto.Steps.Any(s => s.ListBranch);
        AnsiConsole.MarkupLine(
            $"[{Term.Muted}]steps[/] [bold]{auto.Steps.Count}[/]  " +
            $"[{Term.Muted}]fields[/] [bold]{auto.Fields.Count}[/]  " +
            $"[{Term.Muted}]pagination[/] {(auto.HasPagination ? $"[{Term.Ok}]yes[/]" : $"[{Term.Muted}]no[/]")}  " +
            $"[{Term.Muted}]repeat click[/] {(repeat ? $"[{Term.Ok}]yes[/]" : $"[{Term.Muted}]no[/]")}" +
            (dirty ? $"  [{Term.Warn}]● unsaved[/]" : ""));
    }

    private static IEnumerable<MenuItem> BuildMenu(AutomationRecord auto)
    {
        yield return new MenuItem(MenuAction.AddClick, "Add click", "page or card");
        yield return new MenuItem(MenuAction.AddField, "Add field", "text / image / url");
        yield return new MenuItem(MenuAction.MarkNextPage, "Mark next page", "go to next page");

        if (auto.HasPagination)
            yield return new MenuItem(MenuAction.ClearNextPage, "Clear next page", "");

        if (auto.Steps.Count > 0)
        {
            var on = auto.Steps[^1].ListBranch;
            yield return new MenuItem(
                MenuAction.ToggleRepeat,
                on ? "Last click: run once" : "Last click: repeat for every item",
                on ? "repeats now" : "opens each item");
        }

        yield return new MenuItem(MenuAction.UndoStep, "Undo last step", "");
        yield return new MenuItem(MenuAction.UndoField, "Undo last field", "");
        yield return new MenuItem(MenuAction.Status, "Show status", "");
        yield return new MenuItem(MenuAction.Save, "Save", "");
        yield return new MenuItem(MenuAction.Finish, "Done", "quit");
    }

    private static async Task<bool> AddClickAsync(
        AutomationRecord auto,
        IPage page,
        Func<string, TimeSpan, CancellationToken, Task<PickEvent>> waitPick,
        CancellationToken ct)
    {
        Term.InfoMsg("Click the element in the browser…");
        await InjectPickerAsync(page, "click");
        var clickEv = await waitPick("click", TimeSpan.FromMinutes(5), ct);
        await InjectPickerAsync(page, "idle");

        var guess = SelectorHelper.LooksLikeListItem(clickEv.Selector!, clickEv.Text);
        AnsiConsole.MarkupLine(
            $"[{Term.Muted}]Selected:[/] {Term.Escape(Truncate(clickEv.Selector!, 70))}");
        var repeat = Term.Confirm(
            "Repeat this click for every list item? (e.g. open each product detail)",
            defaultValue: guess);

        if (repeat)
        {
            foreach (var s in auto.Steps.Where(s => s.ListBranch))
                s.ListBranch = false;
        }

        auto.Steps.Add(new RecordedStep
        {
            Type = "click",
            Selector = clickEv.Selector!,
            UrlAfter = clickEv.Href ?? clickEv.Url,
            ListBranch = repeat,
        });

        Term.Success(
            repeat
                ? "Click added. Harvest will repeat it for each match."
                : "Click added. It will run once.");
        return true;
    }

    private static async Task<bool> AddFieldAsync(
        AutomationRecord auto,
        IPage page,
        Func<string, TimeSpan, CancellationToken, Task<PickEvent>> waitPick,
        CancellationToken ct)
    {
        var key = Term.AskText("Field key");
        if (auto.Fields.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            Term.WarnMsg($"Key already exists: {key}");
            return false;
        }

        var type = AnsiConsole.Prompt(
            Term.SelectPrompt<string>("Field type")
                .AddChoices(FieldTypes.Text, FieldTypes.Image, FieldTypes.Url)
                .UseConverter(t => t switch
                {
                    FieldTypes.Image => "image  — download image",
                    FieldTypes.Url => "url    — link",
                    _ => "text   — text",
                }));

        Term.InfoMsg($"Click the \"{key}\" ({type}) element in the browser…");
        await InjectPickerAsync(page, "field");
        var ev = await waitPick("field", TimeSpan.FromMinutes(5), ct);
        await InjectPickerAsync(page, "idle");

        auto.Fields.Add(new FieldMapping
        {
            Key = key,
            Type = type,
            Selector = ev.Selector!,
        });
        Term.Success($"Field added: {key} ({type})");
        return true;
    }

    private static async Task<bool> MarkNextPageAsync(
        AutomationRecord auto,
        IPage page,
        Func<string, TimeSpan, CancellationToken, Task<PickEvent>> waitPick,
        CancellationToken ct)
    {
        Term.InfoMsg("Click the next-page control…");
        await InjectPickerAsync(page, "next");
        var nextEv = await waitPick("next", TimeSpan.FromMinutes(5), ct);
        await InjectPickerAsync(page, "idle");
        auto.HasPagination = true;
        auto.NextPageSelector = nextEv.Selector;
        Term.Success($"Next page marked → {Truncate(nextEv.Selector!, 70)}");
        return true;
    }

    private static async Task InjectPickerAsync(IPage page, string mode)
    {
        await page.EvaluateAsync("""
(mode) => {
  function cssPath(el) {
    if (!el || el.nodeType !== 1) return '';
    const parts = [];
    let cur = el;
    while (cur && cur.nodeType === 1 && parts.length < 8) {
      let part = cur.tagName.toLowerCase();
      if (cur.id && /^[A-Za-z][\w\-:.]*$/.test(cur.id)) {
        part += '#' + CSS.escape(cur.id);
        parts.unshift(part);
        break;
      }
      const parent = cur.parentElement;
      if (parent) {
        const siblings = Array.from(parent.children).filter(c => c.tagName === cur.tagName);
        if (siblings.length > 1) {
          const index = siblings.indexOf(cur) + 1;
          part += `:nth-of-type(${index})`;
        }
      }
      const cls = (typeof cur.className === 'string' ? cur.className : '')
        .trim().split(/\s+/).filter(c => c && !c.includes(':') && c.length < 40).slice(0, 2);
      if (cls.length) part += cls.map(c => '.' + CSS.escape(c)).join('');
      parts.unshift(part);
      cur = parent;
      if (cur && (cur.tagName === 'BODY' || cur.tagName === 'HTML')) break;
    }
    return parts.join(' > ');
  }
  window.__siteHarvestCssPath = cssPath;

  if (!window.__siteHarvestBound) {
    window.__siteHarvestBound = true;
    document.addEventListener('click', (e) => {
      const modeNow = window.__siteHarvestMode || 'idle';
      if (modeNow === 'idle') return;
      e.preventDefault();
      e.stopPropagation();
      const selector = window.__siteHarvestCssPath(e.target);
      const a = e.target.closest && e.target.closest('a');
      const href = a ? a.href : (e.target.href || null);
      const text = (e.target.innerText || e.target.textContent || '').trim().slice(0, 120);
      if (window.siteHarvestOnPick) {
        window.siteHarvestOnPick(JSON.stringify({
          mode: modeNow,
          selector,
          href,
          text,
          url: location.href
        }));
      }
    }, true);
  }
  window.__siteHarvestMode = mode;
}
""", mode);
    }

    private static void PrintStatus(AutomationRecord auto)
    {
        AnsiConsole.MarkupLine($"[{Term.Muted}]StartUrl[/] {Term.Escape(auto.StartUrl)}");

        if (auto.Steps.Count == 0)
            Term.Hint("No click steps yet.");
        else
        {
            var steps = Term.NewTable();
            steps.AddColumn("#");
            steps.AddColumn("How often");
            steps.AddColumn("Selector");
            for (var i = 0; i < auto.Steps.Count; i++)
            {
                var s = auto.Steps[i];
                steps.AddRow(
                    $"{i + 1}",
                    s.ListBranch ? $"[{Term.Ok}]every item[/]" : $"[{Term.Muted}]once[/]",
                    Term.Escape(s.Selector));
            }

            AnsiConsole.Write(steps);
        }

        if (auto.Fields.Count == 0)
            Term.Hint("No fields yet.");
        else
        {
            var fields = Term.NewTable();
            fields.AddColumn("Key");
            fields.AddColumn("Type");
            fields.AddColumn("Selector");
            foreach (var f in auto.Fields)
            {
                fields.AddRow(
                    $"[bold]{Term.Escape(f.Key)}[/]",
                    $"[{Term.Info}]{Term.Escape(f.Type)}[/]",
                    Term.Escape(f.Selector));
            }

            AnsiConsole.Write(fields);
        }

        AnsiConsole.MarkupLine(
            $"[{Term.Muted}]Pagination[/] {(auto.HasPagination ? $"[{Term.Ok}]yes[/]" : "no")}  " +
            $"[{Term.Muted}]next[/] {Term.Escape(auto.NextPageSelector ?? "—")}");
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class PickEvent
    {
        public string Mode { get; set; } = "click";
        public string? Selector { get; set; }
        public string? Href { get; set; }
        public string? Text { get; set; }
        public string? Url { get; set; }
    }
}
