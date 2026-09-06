using System.Net;
using System.Text.RegularExpressions;

namespace NexusMods;

internal static class Markup
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private static Regex Pattern(string expression) => new(expression, Options, MatchTimeout);

    // Cache the fixed pipeline; it has more patterns than the shared Regex cache holds by default.
    private static readonly Regex HtmlLink = Pattern(@"<a((?:\s[^>]*)?)>([\s\S]*?)</a\s*>");
    private static readonly Regex Href = Pattern("(?:^|\\s)href\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))");
    private static readonly Regex BbcodeLink = Pattern(@"\[url=([^\]]+)]([\s\S]*?)\[/url]");
    private static readonly (Regex Pattern, string Replacement)[] HtmlRules =
    [
        (Pattern(@"<br\s*/?>"), "\n"),
        (Pattern(@"</?(?:p|div|h[1-6]|li|ul|ol|blockquote)(?:[\s/][^>]*)?>"), "\n"),
        (Pattern(@"</?(?:a|b|strong|i|em|u|s|strike|span|font|center|small|sub|sup|pre|code)(?:[\s/][^>]*)?>"), "")
    ];
    private static readonly (Regex Pattern, string Replacement)[] BbcodeRules =
    [
        (Pattern(@"\[url]([\s\S]*?)\[/url]"), "$1"),
        (Pattern(@"\[youtube]([\s\S]*?)\[/youtube]"), "https://www.youtube.com/watch?v=$1"),
        (Pattern(@"\[img]([\s\S]*?)\[/img]"), "[image: $1]"),
        (Pattern(@"\[\*]"), "\n- "), (Pattern(@"\[/\*]"), ""), (Pattern(@"\[(?:line|hr)]"), "\n---\n"),
        (Pattern(@"\[/?(?:b|i|u|s|strike|font|color|size|center|left|right|justify|list|quote|spoiler|code|table|tr|td|th)(?:=[^\]]*)?]"), "")
    ];
    private static readonly (Regex Pattern, string Replacement)[] WhitespaceRules =
    [
        (Pattern(@"[ \t]+\n"), "\n"), (Pattern(@"\n[ \t]+"), "\n"),
        (Pattern(@"[ \t]{2,}"), " "), (Pattern(@"\n{3,}"), "\n\n")
    ];

    private static string Link(string label, string url)
    {
        var text = label.Trim();
        return text.Length == 0 || text == url ? url : $"{text} ({url})";
    }

    internal static string Text(string markup)
    {
        try
        {
            var text = markup.Replace("\uFEFF", "");
            text = HtmlLink.Replace(text, match =>
            {
                var href = Href.Match(match.Groups[1].Value);
                return href.Success
                    ? Link(match.Groups[2].Value, href.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value)
                    : match.Groups[2].Value;
            });
            foreach (var (pattern, replacement) in HtmlRules) text = pattern.Replace(text, replacement);
            text = BbcodeLink.Replace(text, match => Link(match.Groups[2].Value, match.Groups[1].Value));
            foreach (var (pattern, replacement) in BbcodeRules) text = pattern.Replace(text, replacement);
            text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
            foreach (var (pattern, replacement) in WhitespaceRules) text = pattern.Replace(text, replacement);
            return text.Trim();
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new CliException("MARKUP_PROCESSING_TIMEOUT", "Nexus markup exceeded the processing time limit.", innerException: exception);
        }
    }
}
