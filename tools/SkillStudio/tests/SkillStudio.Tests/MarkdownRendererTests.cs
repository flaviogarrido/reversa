using SkillStudio.Core.Preview;

namespace SkillStudio.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void Renders_headings_lists_tables_and_code()
    {
        var html = MarkdownRenderer.ToHtmlFragment("""
            # Título

            - um
            - dois

            | A | B |
            |---|---|
            | 1 | 2 |

            ```bash
            echo oi
            ```
            """);

        Assert.Contains("<h1", html);
        Assert.Contains("<li>um</li>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<td>2</td>", html);
        Assert.Contains("language-bash", html);
    }

    [Fact]
    public void Raw_html_is_escaped_not_executed()
    {
        var html = MarkdownRenderer.ToHtmlFragment("Texto <script>alert(1)</script> e <img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Page_blocks_scripts_and_remote_content()
    {
        var page = MarkdownRenderer.ToHtmlPage("# Oi", dark: false);

        Assert.Contains("Content-Security-Policy", page);
        Assert.Contains("default-src 'none'", page);
        Assert.Contains("class=\"light\"", page);
        Assert.Contains("class=\"dark\"", MarkdownRenderer.ToHtmlPage("# Oi", dark: true));
    }

    [Fact]
    public void Empty_markdown_shows_placeholder()
    {
        Assert.Contains("Nada para visualizar", MarkdownRenderer.ToHtmlPage("   ", dark: false));
    }

    [Fact]
    public void Frontmatter_is_not_rendered()
    {
        var html = MarkdownRenderer.ToHtmlFragment("---\nname: x\ndescription: y\n---\n# Corpo\n");

        Assert.DoesNotContain("name: x", html);
        Assert.Contains("Corpo", html);
        Assert.Equal("# Sem\n", MarkdownRenderer.StripFrontmatter("# Sem\n"));
    }

    [Theory]
    [InlineData("references/guia.md", true)]
    [InlineData("references/GUIA.MD", true)]
    [InlineData("references/notas.markdown", true)]
    [InlineData("scripts/run.sh", false)]
    [InlineData("assets/logo.png", false)]
    [InlineData(null, false)]
    public void Only_markdown_files_have_preview(string? path, bool expected) =>
        Assert.Equal(expected, MarkdownRenderer.CanPreview(path));
}
