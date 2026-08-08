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
    /// Infer a repeating card root from taught field selector paths only.
    /// Uses the longest common prefix; requires an :nth-* in teaching so list pages
    /// are detected from recorded structure, not from class-name guesses.
    /// </summary>
    public static string? InferRepeatingCardSelector(IEnumerable<string?> fieldSelectors)
    {
        var paths = fieldSelectors
            .Select(Sanitize)
            .Where(s => s != null)
            .Select(s => SplitPath(s!))
            .Where(p => p.Length > 0)
            .ToList();
        if (paths.Count == 0)
            return null;

        // Only treat as a list when teaching left an nth index on at least one path.
        if (!paths.Any(p => p.Any(HasNthIndex)))
            return null;

        var minLen = paths.Min(p => p.Length);
        var lcp = new List<string>();
        for (var i = 0; i < minLen; i++)
        {
            var generalized = GeneralizeListSelector(paths[0][i]);
            if (generalized == null
                || paths.Any(p => GeneralizeListSelector(p[i]) != generalized))
                break;
            lcp.Add(generalized);
        }

        // Too short (e.g. just "div") is unsafe to iterate as cards.
        if (lcp.Count < 2)
            return null;

        return string.Join(" > ", lcp);
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
