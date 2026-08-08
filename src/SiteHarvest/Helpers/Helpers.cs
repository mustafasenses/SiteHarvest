using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteHarvest.Helpers;

public static class SelectorHelper
{
    public static string? Sanitize(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return null;
        return selector.Trim();
    }

    public static string? GeneralizeListSelector(string? selector)
    {
        var s = Sanitize(selector);
        if (s == null)
            return null;

        // Remove :nth-child / :nth-of-type so all sibling cards match.
        s = Regex.Replace(s, @":nth-(?:child|of-type)\(\d+\)", "", RegexOptions.IgnoreCase);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Last segment of a descendant path, with nth indices stripped.</summary>
    public static string? LeafSelector(string? selector)
    {
        var s = Sanitize(selector);
        if (s == null)
            return null;

        var parts = SplitPath(s);
        return parts.Length == 0 ? null : GeneralizeListSelector(parts[^1]);
    }

    /// <summary>
    /// Infer a repeating card root from taught field selector paths.
    /// Prefers the deepest :nth-* ancestor (not the leaf field), so mixed prefixes
    /// (e.g. section#… vs div.section-content…) and page-level fields (h4) still
    /// resolve to the repeating item container.
    /// </summary>
    public static string? InferRepeatingCardSelector(IEnumerable<string?> fieldSelectors)
    {
        string? best = null;
        var bestDepth = 0;

        foreach (var raw in fieldSelectors)
        {
            var parts = SplitPath(Sanitize(raw) ?? "");
            if (parts.Length < 2)
                continue;

            // Prefer nth on a non-leaf segment (the card), not the taught field leaf.
            var nthIdx = -1;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (HasNthIndex(parts[i]))
                    nthIdx = i;
            }

            if (nthIdx < 0)
                continue;

            var cardParts = new List<string>();
            var ok = true;
            for (var i = 0; i <= nthIdx; i++)
            {
                var g = GeneralizeListSelector(parts[i]);
                if (g == null)
                {
                    ok = false;
                    break;
                }

                cardParts.Add(g);
            }

            if (!ok || cardParts.Count < 2)
                continue;

            if (cardParts.Count > bestDepth
                || (cardParts.Count == bestDepth && string.Join(" > ", cardParts).Length > (best?.Length ?? 0)))
            {
                bestDepth = cardParts.Count;
                best = string.Join(" > ", cardParts);
            }
        }

        return best;
    }

    /// <summary>
    /// Suggest "repeat for each list item" in teach UI when the click path has an nth index.
    /// </summary>
    public static bool LooksLikeListItem(string selector, string? text = null)
    {
        _ = text;
        return selector.Contains(":nth-of-type(", StringComparison.OrdinalIgnoreCase)
               || selector.Contains(":nth-child(", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// If the leaf pins a specific element id (e.g. img#hero-3), return tag[id] so
    /// harvest can match peer elements when that exact id is missing on some pages.
    /// Not domain-specific — only relaxes brittle #id targeting.
    /// </summary>
    public static string? RelaxSpecificId(string? selector)
    {
        var leaf = LeafSelector(selector) ?? Sanitize(selector);
        if (leaf == null)
            return null;

        var m = Regex.Match(
            leaf,
            @"^(?<tag>[a-zA-Z][\w-]*)?#(?<id>[\w-]+)$",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;

        var tag = m.Groups["tag"].Success && m.Groups["tag"].Length > 0
            ? m.Groups["tag"].Value
            : "*";
        return $"{tag}[id]";
    }

    /// <summary>
    /// Primary generalized selector, then optional relaxed-id candidate.
    /// </summary>
    public static IReadOnlyList<string> SelectorCandidates(string? selector)
    {
        var list = new List<string>();
        var primary = GeneralizeListSelector(selector) ?? Sanitize(selector);
        if (primary != null)
            list.Add(primary);

        var leafFb = RelaxSpecificId(selector);
        if (leafFb == null)
            return list;

        var parts = SplitPath(primary ?? selector ?? "");
        string candidate;
        if (parts.Length <= 1)
            candidate = leafFb;
        else
        {
            var head = parts.Take(parts.Length - 1)
                .Select(p => GeneralizeListSelector(p) ?? p);
            candidate = string.Join(" > ", head.Append(leafFb));
        }

        if (!list.Exists(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase)))
            list.Add(candidate);

        return list;
    }

    private static string[] SplitPath(string selector) =>
        selector.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool HasNthIndex(string part) =>
        part.Contains(":nth-of-type(", StringComparison.OrdinalIgnoreCase)
        || part.Contains(":nth-child(", StringComparison.OrdinalIgnoreCase);
}

public static class UrlHelper
{
    public static string? ToAbsolute(string? maybeRelative, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(maybeRelative))
            return null;

        var href = maybeRelative.Trim();

        // Paths like /x are page-relative. On Unix, UriKind.Absolute would treat them as files.
        if (href.StartsWith('/') || href.StartsWith("./", StringComparison.Ordinal)
            || href.StartsWith("../", StringComparison.Ordinal)
            || href.StartsWith('#') || href.StartsWith('?'))
        {
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
                && Uri.TryCreate(page, href, out var resolved))
                return resolved.ToString();
            return href;
        }

        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
                && Uri.TryCreate(page, href, out var resolved))
                return resolved.ToString();
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
            return abs.ToString();

        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var basePage)
            && Uri.TryCreate(basePage, href, out var fromBase))
            return fromBase.ToString();

        return href;
    }

    public static string BuildExternalKey(string? pageUrl, string? anchor, string disambiguator)
    {
        var page = (pageUrl ?? "").Trim().ToLowerInvariant();
        var a = (anchor ?? "").Trim().ToLowerInvariant();
        var hint = (disambiguator ?? "").Trim().ToLowerInvariant();
        var raw = !string.IsNullOrWhiteSpace(a) ? $"{page}|{a}" : $"{page}|{hint}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return hash[..24];
    }
}
