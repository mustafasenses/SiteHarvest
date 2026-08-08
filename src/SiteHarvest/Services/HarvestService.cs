using System.Net.Http;
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
            var page = await browser.NewPageAsync(PlaywrightFactory.PageOptions());

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
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReachedMax(items, maxItems))
                break;

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

            if (!await TryGoNextPageAsync(page, auto, ct))
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
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (ReachedMax(items, maxItems))
                break;

            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            Console.WriteLine($"Page {pageIndex + 1}: capture ({page.Url})");
            await CaptureCurrentPageAsync(page, auto, run, items, visitKey: $"p{pageIndex}", maxItems, ct);

            if (ReachedMax(items, maxItems))
                break;

            if (!await TryGoNextPageAsync(page, auto, ct))
                break;

            pageIndex++;
        }
    }

    private static async Task<bool> TryGoNextPageAsync(
        IPage page,
        AutomationRecord auto,
        CancellationToken ct)
    {
        if (!auto.HasPagination || string.IsNullOrWhiteSpace(auto.NextPageSelector))
            return false;

        ct.ThrowIfCancellationRequested();
        var next = page.Locator(auto.NextPageSelector).First;
        if (await next.CountAsync() == 0)
            return false;

        try
        {
            var disabled = await next.GetAttributeAsync("disabled");
            var aria = await next.GetAttributeAsync("aria-disabled");
            if (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(aria, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            await next.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitForTimeoutAsync(500);
            return true;
        }
        catch
        {
            return false;
        }
    }

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
            var imageSelector = SelectorHelper.GeneralizeListSelector(imageField.Selector)
                                ?? imageField.Selector;
            await PrepareMediaAsync(page, imageSelector, ct);

            var locators = page.Locator(imageSelector);
            var count = await locators.CountAsync();
            if (count == 0)
            {
                await CaptureOneAsync(page, auto, run, items, visitKey, cardIndex: 0, imageLocator: null, maxItems, ct);
                return;
            }

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

        await CaptureOneAsync(page, auto, run, items, visitKey, 0, imageLocator: null, maxItems, ct);
    }

    private async Task CaptureOneAsync(
        IPage page,
        AutomationRecord auto,
        RunRecord run,
        List<HarvestItem> items,
        string visitKey,
        int cardIndex,
        ILocator? imageLocator,
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
                var extracted = imageLocator != null
                    ? await ExtractFromCardAsync(imageLocator, field, pageUrl)
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

    private static async Task<string?> ExtractFromCardAsync(
        ILocator imageLocator,
        FieldMapping field,
        string pageUrl)
    {
        var payload = await imageLocator.EvaluateAsync<CardExtractDto?>(
            CardScopedScript,
            new { selector = field.Selector, type = field.Type });

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
  const type = ((args && args.type) || 'text').toLowerCase();

  function findCard(node) {
    let cur = node;
    for (let depth = 0; depth < 10 && cur; depth++) {
      const cls = (cur.className && typeof cur.className === 'string') ? cur.className.toLowerCase() : '';
      const tag = (cur.tagName || '').toLowerCase();
      if (
        tag === 'article' || tag === 'li' ||
        cls.includes('product') || cls.includes('card') || cls.includes('item') ||
        cls.includes('urun') || cls.includes('seri') || cls.includes('tile')
      ) {
        return cur;
      }
      cur = cur.parentElement;
    }
    return node.parentElement || node;
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

  const card = findCard(el);
  let node = null;
  try { node = selector ? card.querySelector(selector) : null; } catch (e) { node = null; }

  if (type === 'image') {
    const img = (node && (node.tagName === 'IMG' ? node : node.querySelector && node.querySelector('img')))
      || (el.tagName === 'IMG' ? el : null)
      || (card.querySelector && card.querySelector('img'));
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
