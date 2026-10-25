using NPMC.Launcher.Core.Models;
using Xunit;

namespace NPMC.Launcher.Core.Tests;

/// <summary>Markdown → 纯文本摘要。详情面板靠它显示"这是个什么模组"。</summary>
public class MarkdownSummaryTests
{
    [Fact]
    public void Images_and_html_are_dropped()
    {
        var text = MarkdownSummary.PlainText("![banner](https://cdn/banner.png)<br/>A fast renderer.");

        Assert.DoesNotContain("banner.png", text);
        Assert.DoesNotContain("<br", text);
        Assert.Contains("A fast renderer.", text);
    }

    [Fact]
    public void Links_keep_their_text()
    {
        var text = MarkdownSummary.PlainText("See [the wiki](https://example/wiki) for details.");

        Assert.Contains("the wiki", text);
        Assert.DoesNotContain("https://example", text);
    }

    [Fact]
    public void Headings_lists_and_emphasis_are_stripped()
    {
        var text = MarkdownSummary.PlainText("## Features\n- **Fast** rendering\n- *Smooth* frames");

        Assert.Contains("Features", text);
        Assert.Contains("Fast rendering", text);
        Assert.Contains("Smooth frames", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("*", text);
    }

    [Fact]
    public void Code_fences_are_removed()
    {
        var text = MarkdownSummary.PlainText("Before\n```\nsecret code\n```\nAfter");

        Assert.Contains("Before", text);
        Assert.Contains("After", text);
        Assert.DoesNotContain("secret code", text);
    }

    [Fact]
    public void Long_text_is_truncated_with_an_ellipsis()
    {
        var text = MarkdownSummary.PlainText(new string('a', 2000), maxLength: 100);

        Assert.Equal(101, text.Length);
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Whitespace_is_collapsed()
    {
        var text = MarkdownSummary.PlainText("line one\n\n\nline   two");

        Assert.Equal("line one line two", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_empty_output(string? markdown)
    {
        Assert.Equal("", MarkdownSummary.PlainText(markdown));
    }
}
