using System.Net;
using System.Text.RegularExpressions;

namespace Symphony.Core.Util;

/// <summary>Tracker rich text helpers shared by adapters.</summary>
public static partial class Html
{
    /// <summary>Plain text from tracker HTML: line breaks for block ends, "- " for list items, entities decoded.</summary>
    public static string? ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = BreakTags().Replace(html, "\n");
        s = ListItem().Replace(s, "\n- ");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s).Replace('\u00A0', ' ');
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/tr)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex ManyNewlines();
}
