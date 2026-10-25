using System.Text;
using System.Text.RegularExpressions;

namespace NPMC.Launcher.Core.Models;

/// <summary>
/// 把 Markdown 正文压成一段可读的纯文本。
///
/// 为什么需要：Modrinth 的项目详情（body）是 Markdown，里面混着图片、链接、标题符号、
/// 代码块与徽章。原样显示会是一堆 <c>![icon](...)</c> 与 <c>###</c>，
/// 而渲染 Markdown 需要完整的渲染器 —— 对一个"看清楚这是哪个模组"的需求来说太重了。
/// 这里只做够用的降级：去掉图片与符号，链接保留文字，压平空白，再截断。
/// </summary>
public static class MarkdownSummary
{
    private static readonly Regex ImagePattern = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex HtmlTagPattern = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CodeFencePattern = new(@"```.*?```", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex InlineCodePattern = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex HeadingPattern = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ListMarkerPattern = new(@"^\s{0,3}([-*+]|\d+\.)\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex EmphasisPattern = new(@"[*_~]{1,3}", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public static string PlainText(string? markdown, int maxLength = 600)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = markdown;

        text = CodeFencePattern.Replace(text, " ");
        text = ImagePattern.Replace(text, " ");
        text = LinkPattern.Replace(text, "$1");
        text = HtmlTagPattern.Replace(text, " ");
        text = InlineCodePattern.Replace(text, "$1");
        text = HeadingPattern.Replace(text, "");
        text = ListMarkerPattern.Replace(text, "· ");
        text = EmphasisPattern.Replace(text, "");

        // 引用块与表格竖线也去掉，保留文字
        text = text.Replace("&gt;", "").Replace("&lt;", "").Replace("&amp;", "&");
        text = text.Replace("|", " ").Replace(">", " ");

        text = WhitespacePattern.Replace(text, " ").Trim();

        if (maxLength > 0 && text.Length > maxLength)
            text = text[..maxLength].TrimEnd() + "…";

        return text;
    }
}
