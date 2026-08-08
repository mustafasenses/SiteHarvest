using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SiteHarvest.Browser;
using SiteHarvest.Helpers;
using SiteHarvest.Models;
using SiteHarvest.Storage;

namespace SiteHarvest.Services;

public sealed class HarvestService
{
    private readonly JsonStore _store;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public HarvestService(JsonStore store) => _store = store;

    public async Task<RunRecord> RunAsync(
        string automationIdOrName,
        int? maxItems = null,
        bool headless = true,
        CancellationToken ct = default)
    {
        var auto = _store.GetAutomation(automationIdOrName)
                   ?? throw new InvalidOperationException($"Automation not found: {automationIdOrName}");

        var run = new RunRecord
        {
            Id = JsonStore.NewId("run"),
            AutomationId = auto.Id,
            SiteId = auto.SiteId,
            Status = "running",
            StartUrl = auto.StartUrl,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _store.SaveRun(run);
        Directory.CreateDirectory(_store.RunMediaDir(run.Id));

        var items = new List<HarvestItem>();
        Console.WriteLine($"Run {run.Id} starting → {auto.Name}");
        Console.WriteLine(maxItems is > 0
            ? $"Limit: at most {maxItems} item(s)"
            : "No limit: run until the end (all pages if pagination is set)");

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(
                PlaywrightFactory.LaunchOptions(headless));
            var sessionPath = _store.SessionPath(auto.SiteId);
            if (_store.HasSession(auto.SiteId))
                Console.WriteLine($"Using saved login session for site {auto.SiteId}");
            else
                Console.WriteLine("No saved login session (fresh browser).");

            await using var context = await browser.NewContextAsync(
                PlaywrightFactory.ContextOptions(
                    _store.HasSession(auto.SiteId) ? sessionPath : null));
            var page = await context.NewPageAsync();

            await page.GotoAsync(auto.StartUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });

            var listBranchIndex = auto.Steps.FindIndex(s => s.ListBranch);
            if (listBranchIndex < 0)
            {
                await PlayStepsAsync(page, auto.Steps, ct);
                await HarvestListingPagesAsync(page, auto, run, items, maxItems, ct);
            }
            else
            {
                await HarvestWithListBranchAsync(page, auto, run, items, listBranchIndex, maxItems, ct);
            }

            run.FoundCount = items.Count;
            run.ItemCount = items.Count;
            run.Status = "succeeded";
            run.FinishedAt = DateTimeOffset.UtcNow;
            _store.SaveItems(run.Id, items);
            _store.SaveRun(run);
            if (maxItems is > 0 && items.Count >= maxItems.Value)
                Console.WriteLine($"Reached max limit ({maxItems}).");
            Console.WriteLine($"Done: {items.Count} item(s) → {_store.RunDir(run.Id)}");
            return run;
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.ErrorMessage = ex.Message;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.FoundCount = items.Count;
            run.ItemCount = items.Count;
            _store.SaveItems(run.Id, items);
            _store.SaveRun(run);
            Console.WriteLine($"Run failed: {ex.Message}");
            throw;
        }
    }

    private async Task HarvestWithListBranchAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        int listBranchIndex,
        int? maxItems,
        CancellationToken ct)
    {
        var prefix = auto.Steps.Take(listBranchIndex).ToList();
        var branchStep = auto.Steps[listBranchIndex];
        var suffix = auto.Steps.Skip(listBranchIndex + 1).ToList();

        await PlayStepsAsync(page, prefix, ct);

        var listingUrl = page.Url;
        var cardSelector = SelectorHelper.GeneralizeListSelector(branchStep.Selector)
                           ?? branchStep.Selector;

        var pageIndex = 0;
        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizePageUrl(listingUrl) };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReachedMax(items, maxItems))
                break;
            if (pageIndex >= MaxPaginationPages)
            {
                Console.WriteLine($"Stopped: reached pagination safety limit ({MaxPaginationPages} pages).");
                break;
            }

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            var cards = page.Locator(cardSelector);
            var count = await cards.CountAsync();
            Console.WriteLine($"List page {pageIndex + 1}: {count} card(s) ({cardSelector})");

            var hrefs = new List<string?>();
            for (var i = 0; i < count; i++)
            {
                var card = cards.Nth(i);
                string? href = null;
                try
                {
                    href = await card.GetAttributeAsync("href");
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        var link = card.Locator("a[href]").First;
                        if (await link.CountAsync() > 0)
                            href = await link.GetAttributeAsync("href");
                    }
                }
                catch
                {
                }

                hrefs.Add(UrlHelper.ToAbsolute(href, page.Url));
            }

            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (ReachedMax(items, maxItems))
                    break;

                var visitKey = $"p{pageIndex}:c{i}";
                try
                {
                    if (!string.IsNullOrWhiteSpace(hrefs[i]))
                    {
                        await page.GotoAsync(hrefs[i]!, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.DOMContentLoaded,
                            Timeout = 60_000,
                        });
                    }
                    else
                    {
                        if (!string.Equals(page.Url, listingUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            await page.GotoAsync(listingUrl, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 60_000,
                            });
                        }

                        await page.Locator(cardSelector).Nth(i).ClickAsync(new LocatorClickOptions
                        {
                            Timeout = 30_000,
                        });
                        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    }

                    await PlayStepsAsync(page, suffix, ct);
                    await CaptureCurrentPageAsync(page, auto, run, items, visitKey, maxItems, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Skipped card {i}: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (!string.Equals(page.Url, listingUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            await page.GotoAsync(listingUrl, new PageGotoOptions
                            {
                                WaitUntil = WaitUntilState.DOMContentLoaded,
                                Timeout = 60_000,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Failed to return to listing: {ex.Message}");
                    }
                }
            }

            if (ReachedMax(items, maxItems))
                break;

            if (!await TryGoNextPageAsync(page, auto, visitedPages, ct))
                break;

            listingUrl = page.Url;
            pageIndex++;
        }
    }

    private async Task HarvestListingPagesAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        int? maxItems,
        CancellationToken ct)
    {
        var pageIndex = 0;
        var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizePageUrl(page.Url) };
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReachedMax(items, maxItems))
                break;
            if (pageIndex >= MaxPaginationPages)
            {
                Console.WriteLine($"Stopped: reached pagination safety limit ({MaxPaginationPages} pages).");
                break;
            }

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5_000 });
            }
            catch
            {
            }

            await page.WaitForTimeoutAsync(400);
            Console.WriteLine($"Page {pageIndex + 1}: capture ({page.Url})");
            await CaptureCurrentPageAsync(page, auto, run, items, visitKey: $"p{pageIndex}", maxItems, ct);

            if (ReachedMax(items, maxItems))
                break;

            if (!await TryGoNextPageAsync(page, auto, visitedPages, ct))
                break;

            pageIndex++;
        }
    }

    private const int MaxPaginationPages = 500;

    private static async Task<bool> TryGoNextPageAsync(
        IPage page,
        AutomationRecord auto,
        HashSet<string> visitedPages,
        CancellationToken ct)
    {
        if (!auto.HasPagination || string.IsNullOrWhiteSpace(auto.NextPageSelector))
            return false;

        ct.ThrowIfCancellationRequested();
        var selector = auto.NextPageSelector;
        var beforeUrl = page.Url;
        var beforeFp = await PageFingerprintAsync(page);

        try
        {
            var numbered = await TryClickNumberedNextAsync(page, selector);
            if (numbered == NumberedPageResult.LastPage)
            {
                Console.WriteLine("Pagination: last page reached (no higher page number).");
                return false;
            }

            if (numbered == NumberedPageResult.NotApplicable)
            {
                if (!await TryClickNextControlAsync(page, selector))
                    return false;
            }

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5_000 });
            }
            catch
            {
            }

            await page.WaitForTimeoutAsync(500);

            var afterUrl = page.Url;
            var afterFp = await PageFingerprintAsync(page);
            if (string.Equals(beforeFp, afterFp, StringComparison.Ordinal)
                && string.Equals(NormalizePageUrl(beforeUrl), NormalizePageUrl(afterUrl), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Pagination: page did not change after next — stopping.");
                return false;
            }

            var normalized = NormalizePageUrl(afterUrl);
            if (!visitedPages.Add(normalized))
            {
                Console.WriteLine("Pagination: already visited this URL — stopping.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Pagination: next failed ({ex.Message}) — stopping.");
            return false;
        }
    }

    private enum NumberedPageResult
    {
        Advanced,
        LastPage,
        NotApplicable,
    }

    /// <summary>
    /// When the taught control sits in a numeric pager (1 2 3 …), click current+1.
    /// Works without a dedicated "next" arrow.
    /// </summary>
    private static async Task<NumberedPageResult> TryClickNumberedNextAsync(IPage page, string taughtSelector)
    {
        PaginationClickDto? result;
        try
        {
            result = await page.EvaluateAsync<PaginationClickDto?>(NumberedPaginationScript, taughtSelector);
        }
        catch
        {
            return NumberedPageResult.NotApplicable;
        }

        if (result == null || string.Equals(result.Mode, "control", StringComparison.OrdinalIgnoreCase))
            return NumberedPageResult.NotApplicable;

        if (result.Ok)
        {
            Console.WriteLine($"Pagination: page numbers {result.Current} → {result.Next}");
            return NumberedPageResult.Advanced;
        }

        if (string.Equals(result.Reason, "last-page", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Reason, "next-disabled", StringComparison.OrdinalIgnoreCase))
            return NumberedPageResult.LastPage;

        return NumberedPageResult.NotApplicable;
    }

    private static async Task<bool> TryClickNextControlAsync(IPage page, string selector)
    {
        var next = page.Locator(selector).First;
        if (await next.CountAsync() == 0)
            return false;

        try
        {
            if (await IsEffectivelyDisabledAsync(next))
                return false;

            await next.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsEffectivelyDisabledAsync(ILocator loc)
    {
        try
        {
            if (await loc.IsDisabledAsync())
                return true;
        }
        catch
        {
        }

        var disabled = await loc.GetAttributeAsync("disabled");
        // HTML boolean attribute: present as "" or "disabled" or "true"
        if (disabled != null)
            return true;

        var aria = await loc.GetAttributeAsync("aria-disabled");
        if (string.Equals(aria, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        var cls = await loc.GetAttributeAsync("class") ?? "";
        if (Regex.IsMatch(cls, @"\b(disabled|is-disabled|btn-disabled|pagination-disabled)\b", RegexOptions.IgnoreCase))
            return true;

        try
        {
            var parentDisabled = await loc.EvaluateAsync<bool>("""
el => {
  const p = el.closest('.disabled, .is-disabled, [aria-disabled="true"], [disabled]');
  return !!p;
}
""");
            if (parentDisabled)
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static async Task<string> PageFingerprintAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>("""
() => {
  const href = location.href.split('#')[0];
  const text = (document.body && (document.body.innerText || '')) || '';
  const sample = text.replace(/\s+/g, ' ').trim().slice(0, 800);
  return href + '|' + text.length + '|' + sample;
}
""") ?? page.Url;
        }
        catch
        {
            return page.Url;
        }
    }

    private static string NormalizePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        try
        {
            var u = new Uri(url);
            var builder = new UriBuilder(u) { Fragment = "" };
            return builder.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url.Split('#')[0].TrimEnd('/');
        }
    }

    private sealed class PaginationClickDto
    {
        public string? Mode { get; set; }
        public bool Ok { get; set; }
        public string? Reason { get; set; }
        public int? Current { get; set; }
        public int? Next { get; set; }
    }

    private const string NumberedPaginationScript = """
(sel) => {
  const PAGE_RE = /^\s*\d{1,4}\s*$/;
  const CURRENT_RE = /\b(active|current|selected|is-active|is-current|is-selected|on|here)\b/i;

  function textOf(el) {
    return ((el && (el.innerText || el.textContent)) || '').replace(/\s+/g, ' ').trim();
  }

  function pageNum(el) {
    const t = textOf(el);
    if (!PAGE_RE.test(t)) return null;
    const n = parseInt(t, 10);
    return Number.isFinite(n) && n > 0 ? n : null;
  }

  function clickTarget(el) {
    if (!el) return null;
    try {
      if (el.matches && el.matches('a, button, [role="button"], [onclick]')) return el;
    } catch (e) {}
    const wrap = el.closest && el.closest('a, button, [role="button"]');
    return wrap || el;
  }

  function isCurrent(el) {
    if (!el) return false;
    const cur = el.getAttribute && el.getAttribute('aria-current');
    if (cur === 'page' || cur === 'true') return true;
    if (el.getAttribute && el.getAttribute('aria-selected') === 'true') return true;
    const cls = (el.className && String(el.className)) || '';
    if (CURRENT_RE.test(cls)) return true;
    const p = el.parentElement;
    if (p) {
      const pcls = (p.className && String(p.className)) || '';
      if (CURRENT_RE.test(pcls)) return true;
      const pcur = p.getAttribute && p.getAttribute('aria-current');
      if (pcur === 'page' || pcur === 'true') return true;
    }
    return false;
  }

  function isDisabled(el) {
    if (!el) return true;
    if (el.disabled === true) return true;
    if (el.getAttribute && el.getAttribute('disabled') != null) return true;
    if (el.getAttribute && el.getAttribute('aria-disabled') === 'true') return true;
    const cls = ((el.className && String(el.className)) || '') + ' ' +
      ((el.parentElement && el.parentElement.className && String(el.parentElement.className)) || '');
    if (/\b(disabled|is-disabled|btn-disabled)\b/i.test(cls)) return true;
    return false;
  }

  let taught = null;
  try { taught = document.querySelector(sel); } catch (e) {
    return { mode: 'control', ok: false, reason: 'bad-selector' };
  }
  if (!taught) return { mode: 'control', ok: false, reason: 'missing' };

  let best = null;
  let node = taught;
  for (let depth = 0; depth < 8 && node; depth++) {
    const candidates = Array.from(node.querySelectorAll('a, button, span, li, div, em, strong'));
    const byNum = new Map();
    for (const c of candidates) {
      const n = pageNum(c);
      if (n == null) continue;
      const target = clickTarget(c);
      if (!target) continue;
      const existing = byNum.get(n);
      if (!existing || (target.tagName === 'A' && existing.tagName !== 'A'))
        byNum.set(n, target);
    }
    if (byNum.size >= 2) {
      best = byNum;
      break;
    }
    node = node.parentElement;
  }

  if (!best) return { mode: 'control', ok: false };

  let current = null;
  for (const [n, el] of best.entries()) {
    if (isCurrent(el)) { current = n; break; }
  }

  if (current == null) {
    for (const [n, el] of best.entries()) {
      let href = null;
      try { href = el.href || (el.getAttribute && el.getAttribute('href')); } catch (e) {}
      if (!href || href === '#' || /^javascript:/i.test(href)) continue;
      try {
        const abs = new URL(href, location.href);
        const here = new URL(location.href);
        if (abs.pathname === here.pathname && abs.search === here.search) {
          current = n;
          break;
        }
      } catch (e) {}
    }
  }

  if (current == null) {
    try {
      const u = new URL(location.href);
      for (const key of ['page', 'p', 'pg', 'sayfa']) {
        const v = u.searchParams.get(key);
        if (v && /^\d+$/.test(v)) {
          const n = parseInt(v, 10);
          if (n === 0 && best.has(1)) { current = 1; break; }
          if (best.has(n)) { current = n; break; }
        }
      }
      if (current == null) {
        const m = location.pathname.match(/\/(?:page|sayfa|p)\/(\d+)/i);
        if (m) {
          const n = parseInt(m[1], 10);
          if (best.has(n)) current = n;
        }
      }
      if (current == null) {
        const hits = [];
        for (const n of best.keys()) {
          const re = new RegExp('(?:[?&](?:page|p|pg|sayfa)=|/(?:page|p|sayfa)/)' + n + '(?:\\D|$)', 'i');
          if (re.test(location.href)) hits.push(n);
        }
        if (hits.length === 1) current = hits[0];
        else if (hits.length > 1) current = Math.max(...hits);
      }
    } catch (e) {}
  }

  if (current == null) {
    for (const [n, el] of best.entries()) {
      if (el.tagName !== 'A' && el.tagName !== 'BUTTON') { current = n; break; }
    }
  }

  // First listing page often has no active marker and no page= in the URL.
  if (current == null && best.has(1)
      && !/[?&](?:page|p|pg|sayfa)=|[\/](?:page|p|sayfa)\/\d+/i.test(location.href)) {
    current = 1;
  }

  if (current == null)
    return { mode: 'control', ok: false, reason: 'unknown-current' };

  const nextNum = current + 1;
  if (best.has(nextNum)) {
    const nextEl = best.get(nextNum);
    if (isDisabled(nextEl))
      return { mode: 'numbers', ok: false, reason: 'next-disabled', current, next: nextNum };
    nextEl.click();
    return { mode: 'numbers', ok: true, current, next: nextNum };
  }

  // Sliding window (e.g. 8 9 10 … 20): page N+1 may be off-screen — use nearby next arrow.
  const arrow = findNextArrow(node || taught.parentElement || taught);
  if (arrow && !isDisabled(arrow)) {
    arrow.click();
    return { mode: 'numbers', ok: true, current, next: nextNum };
  }

  return { mode: 'numbers', ok: false, reason: 'last-page', current, next: nextNum };

  function findNextArrow(root) {
    if (!root) return null;
    const scope = root.closest
      ? (root.closest('nav, .pagination, .pager, ul, ol, div') || root)
      : root;
    const nodes = Array.from(scope.querySelectorAll('a, button, [role="button"], span'));
    const nextRe = /^(next|sonraki|ileri|›|»|→|>|>>)$/i;
    const labelRe = /\b(next|sonraki|ileri)\b/i;
    for (const el of nodes) {
      const t = textOf(el);
      const label = ((el.getAttribute && (el.getAttribute('aria-label') || el.getAttribute('title'))) || '');
      const rel = ((el.getAttribute && el.getAttribute('rel')) || '').toLowerCase();
      if (rel === 'next' || nextRe.test(t) || labelRe.test(label)) {
        const target = clickTarget(el);
        if (target && pageNum(target) == null) return target;
      }
    }
    return null;
  }
}
""";

    private static bool ReachedMax(List<HarvestItem> items, int? maxItems) =>
        maxItems is > 0 && items.Count >= maxItems.Value;

    private static async Task PlayStepsAsync(
        IPage page,
        IEnumerable<RecordedStep> steps,
        CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(step.Type, "click", StringComparison.OrdinalIgnoreCase))
                continue;
            var selector = SelectorHelper.Sanitize(step.Selector);
            if (string.IsNullOrWhiteSpace(selector))
                continue;

            await page.Locator(selector).First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        }
    }

    private async Task CaptureCurrentPageAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        string visitKey,
        int? maxItems,
        CancellationToken ct)
    {
        if (auto.Fields.Count == 0)
        {
            if (ReachedMax(items, maxItems))
                return;

            items.Add(new HarvestItem
            {
                ExternalKey = UrlHelper.BuildExternalKey(page.Url, null, visitKey),
                PageUrl = page.Url,
                Index = items.Count,
            });
            return;
        }

        var imageField = auto.Fields.FirstOrDefault(f =>
            string.Equals(f.Type, FieldTypes.Image, StringComparison.OrdinalIgnoreCase));
        if (imageField != null)
        {
            var prepareSel = SelectorHelper.GeneralizeListSelector(imageField.Selector)
                             ?? imageField.Selector;
            await PrepareMediaAsync(page, prepareSel, ct);
        }

        // Prefer repeating roots from taught selector structure (all fields).
        var cardSelector = SelectorHelper.InferRepeatingCardSelector(auto.Fields.Select(f => f.Selector));
        if (cardSelector != null)
        {
            var cards = page.Locator(cardSelector);
            var cardCount = await cards.CountAsync();
            if (cardCount > 0)
            {
                await CaptureEachAsync(
                    page, auto, run, items, visitKey, cards, cardCount, maxItems, ct);
                return;
            }
        }

        // Fallback: generalized image field matches (taught image selector).
        if (imageField != null)
        {
            var imageSelector = SelectorHelper.GeneralizeListSelector(imageField.Selector)
                                ?? imageField.Selector;
            var locators = page.Locator(imageSelector);
            var count = await locators.CountAsync();
            if (count > 1)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    if (ReachedMax(items, maxItems))
                        break;
                    ct.ThrowIfCancellationRequested();

                    var loc = locators.Nth(i);
                    try
                    {
                        await loc.ScrollIntoViewIfNeededAsync();
                        await page.WaitForTimeoutAsync(150);
                    }
                    catch
                    {
                    }

                    await CaptureOneAsync(
                        page, auto, run, items, $"{visitKey}:{i}", i, loc, maxItems, ct, seen);
                }

                return;
            }
        }

        // Fallback: each match of the first taught field = one item.
        var driver = auto.Fields[0];
        var driverSel = SelectorHelper.GeneralizeListSelector(driver.Selector) ?? driver.Selector;
        var driverLocs = page.Locator(driverSel);
        var driverCount = await driverLocs.CountAsync();
        if (driverCount > 1)
        {
            await CaptureEachAsync(
                page, auto, run, items, visitKey, driverLocs, driverCount, maxItems, ct);
            return;
        }

        await CaptureOneAsync(page, auto, run, items, visitKey, 0, cardRoot: null, maxItems, ct);
    }

    private async Task CaptureEachAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        string visitKey,
        ILocator roots,
        int count,
        int? maxItems,
        CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            if (ReachedMax(items, maxItems))
                break;
            ct.ThrowIfCancellationRequested();

            var root = roots.Nth(i);
            try
            {
                await root.ScrollIntoViewIfNeededAsync();
                await page.WaitForTimeoutAsync(100);
            }
            catch
            {
            }

            await CaptureOneAsync(
                page, auto, run, items, $"{visitKey}:{i}", i, cardRoot: root, maxItems, ct);
        }
    }

    private async Task CaptureOneAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        string visitKey,
        int cardIndex,
        ILocator? cardRoot,
        int? maxItems,
        CancellationToken ct,
        HashSet<string>? seenImageUrls = null)
    {
        if (ReachedMax(items, maxItems))
            return;

        var pageUrl = page.Url;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? anchor = null;

        foreach (var field in auto.Fields)
        {
            types[field.Key] = field.Type;
            try
            {
                var extracted = cardRoot != null
                    ? await ExtractFromCardRootAsync(cardRoot, field, pageUrl)
                    : await ExtractFromPageAsync(page, field, pageUrl);

                if (string.Equals(field.Type, FieldTypes.Image, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(extracted))
                {
                    if (seenImageUrls != null && !seenImageUrls.Add(extracted))
                        return;

                    anchor ??= extracted;
                    var local = await DownloadImageAsync(extracted, pageUrl, run.Id, field.Key, cardIndex, ct);
                    values[field.Key] = local;
                }
                else
                {
                    values[field.Key] = extracted;
                    if (string.Equals(field.Type, FieldTypes.Url, StringComparison.OrdinalIgnoreCase))
                        anchor ??= extracted;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Field '{field.Key}' left empty: {ex.Message}");
                values[field.Key] = null;
            }
        }

        var item = new HarvestItem
        {
            ExternalKey = UrlHelper.BuildExternalKey(pageUrl, anchor, visitKey),
            PageUrl = pageUrl,
            Index = items.Count,
            Values = values,
            Types = types,
        };
        items.Add(item);
        Console.WriteLine($"  + item #{items.Count} {item.ExternalKey}");
    }

    private static async Task<string?> ExtractFromPageAsync(IPage page, FieldMapping field, string pageUrl)
    {
        var selector = SelectorHelper.GeneralizeListSelector(field.Selector) ?? field.Selector;
        var loc = page.Locator(selector).First;
        if (await loc.CountAsync() == 0)
            return null;

        return await ReadLocatorAsync(loc, field.Type, pageUrl);
    }

    private static async Task<string?> ExtractFromCardRootAsync(
        ILocator cardRoot,
        FieldMapping field,
        string pageUrl)
    {
        // Prefer leaf selector scoped to this card (full page paths never match inside a card).
        var leaf = SelectorHelper.LeafSelector(field.Selector);
        if (!string.IsNullOrWhiteSpace(leaf))
        {
            var loc = cardRoot.Locator(leaf).First;
            if (await loc.CountAsync() > 0)
                return await ReadLocatorAsync(loc, field.Type, pageUrl);

            // Card root may already be the field node (LCP ended at the leaf).
            try
            {
                var isSelf = await cardRoot.EvaluateAsync<bool>(
                    "(el, sel) => { try { return el.matches(sel); } catch (e) { return false; } }",
                    leaf);
                if (isSelf)
                    return await ReadLocatorAsync(cardRoot, field.Type, pageUrl);
            }
            catch
            {
            }
        }

        return await ExtractFromCardAsync(cardRoot, field, pageUrl);
    }

    private static async Task<string?> ExtractFromCardAsync(
        ILocator cardRoot,
        FieldMapping field,
        string pageUrl)
    {
        var leaf = SelectorHelper.LeafSelector(field.Selector) ?? field.Selector;
        var payload = await cardRoot.EvaluateAsync<CardExtractDto?>(
            CardScopedScript,
            new { selector = field.Selector, leaf, type = field.Type });

        if (payload == null)
            return null;

        return field.Type.ToLowerInvariant() switch
        {
            FieldTypes.Image => UrlHelper.ToAbsolute(payload.Value, pageUrl) ?? payload.Value,
            FieldTypes.Url => UrlHelper.ToAbsolute(payload.Value, pageUrl) ?? payload.Value,
            _ => payload.Value,
        };
    }

    private static async Task<string?> ReadLocatorAsync(ILocator loc, string type, string pageUrl)
    {
        var t = type.ToLowerInvariant();
        if (t == FieldTypes.Image)
        {
            var src = await loc.GetAttributeAsync("src")
                      ?? await loc.GetAttributeAsync("data-src");
            if (string.IsNullOrWhiteSpace(src))
            {
                var img = loc.Locator("img").First;
                if (await img.CountAsync() > 0)
                    src = await img.GetAttributeAsync("src") ?? await img.GetAttributeAsync("data-src");
            }

            return UrlHelper.ToAbsolute(src, pageUrl);
        }

        if (t == FieldTypes.Url)
        {
            var href = await loc.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                var a = loc.Locator("a[href]").First;
                if (await a.CountAsync() > 0)
                    href = await a.GetAttributeAsync("href");
            }

            return UrlHelper.ToAbsolute(href, pageUrl);
        }

        var text = await loc.InnerTextAsync();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task<string?> DownloadImageAsync(
        string absoluteUrl,
        string pageUrl,
        string runId,
        string fieldKey,
        int index,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
            req.Headers.TryAddWithoutValidation("Referer", pageUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "site-harvest/1.0");
            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
                return null;

            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return null;

            var ext = GuessExt(res.Content.Headers.ContentType?.MediaType, absoluteUrl);
            var safeKey = SanitizeFilePart(fieldKey);
            var fileName = $"{safeKey}_{index}_{Guid.NewGuid().ToString("N")[..8]}{ext}";
            var path = Path.Combine(_store.RunMediaDir(runId), fileName);
            await File.WriteAllBytesAsync(path, bytes, ct);
            return Path.Combine("media", fileName).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Image download failed ({absoluteUrl}): {ex.Message}");
            return null;
        }
    }

    private static string GuessExt(string? mediaType, string url)
    {
        if (mediaType != null)
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            if (mediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
                return ".jpg";
        }

        var ext = Path.GetExtension(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext.Split('?')[0];
    }

    private static string SanitizeFilePart(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return string.IsNullOrWhiteSpace(s) ? "img" : s[..Math.Min(40, s.Length)];
    }

    private static async Task PrepareMediaAsync(IPage page, string imageSelector, CancellationToken ct)
    {
        try
        {
            var locs = page.Locator(imageSelector);
            var n = Math.Min(await locs.CountAsync(), 40);
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                try { await locs.Nth(i).ScrollIntoViewIfNeededAsync(); }
                catch { }
                await page.WaitForTimeoutAsync(80);
            }
        }
        catch
        {
        }
    }

    private sealed class CardExtractDto
    {
        public string? Value { get; set; }
    }

    private const string CardScopedScript = """
(el, args) => {
  const selector = (args && args.selector) || '';
  const leaf = (args && args.leaf) || '';
  const type = ((args && args.type) || 'text').toLowerCase();

  function queryIn(root, sel) {
    if (!sel || !root || !root.querySelector) return null;
    try { return root.querySelector(sel); } catch (e) { return null; }
  }

  // Walk up from the taught node; prefer the smallest ancestor where the leaf matches once.
  function resolveNode(start, fullSel, leafSel) {
    let cur = start;
    let best = null;
    for (let depth = 0; depth < 15 && cur; depth++) {
      try {
        if (leafSel && cur.matches && cur.matches(leafSel)) return cur;
      } catch (e) {}

      let hit = queryIn(cur, fullSel) || queryIn(cur, leafSel);
      if (hit) {
        let count = 0;
        try {
          count = leafSel ? cur.querySelectorAll(leafSel).length
            : (fullSel ? cur.querySelectorAll(fullSel).length : 0);
        } catch (e) { count = hit ? 1 : 0; }
        if (count === 1) best = hit;
        if (count > 1) break;
      }
      cur = cur.parentElement;
    }
    return best || queryIn(start, leafSel) || queryIn(start, fullSel);
  }

  function pickImg(img) {
    if (!img) return null;
    const candidates = [
      img.currentSrc, img.src,
      img.getAttribute('data-src'),
      img.getAttribute('data-original'),
      img.getAttribute('data-lazy'),
      img.getAttribute('data-url'),
    ];
    for (const c of candidates) {
      if (c && typeof c === 'string' && c.trim() && !c.startsWith('data:')) return c.trim();
    }
    return null;
  }

  const node = resolveNode(el, selector, leaf);

  if (type === 'image') {
    const img = (node && (node.tagName === 'IMG' ? node : node.querySelector && node.querySelector('img')))
      || (el.tagName === 'IMG' ? el : null);
    return { value: pickImg(img) };
  }

  if (type === 'url') {
    const a = node && (node.tagName === 'A' ? node : (node.closest && node.closest('a')) || (node.querySelector && node.querySelector('a')));
    const href = a ? a.href : null;
    return { value: href || null };
  }

  if (!node) return { value: null };
  const text = (node.innerText || node.textContent || '').replace(/\s+/g, ' ').trim();
  return { value: text || null };
}
""";
}
