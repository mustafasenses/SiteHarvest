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

    public static bool LooksLikeListItem(string selector, string? text = null)
    {
        var hay = $"{selector} {text ?? ""}".ToLowerInvariant();
        return selector.Contains(":nth-of-type(", StringComparison.Ordinal)
               || selector.Contains(":nth-child(", StringComparison.Ordinal)
               || hay.Contains("product")
               || hay.Contains("card")
               || hay.Contains("item")
               || hay.Contains("ürün");
    }
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
