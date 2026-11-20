using System.Text.RegularExpressions;
using Markdig;

namespace SkillStudio.Core.Preview;

/// <summary>
/// Converte o Markdown das instruções e das referências numa página HTML completa para a
/// pré-visualização do editor. O conteúdo vem de skills de terceiros, então a página não executa
/// nada: HTML bruto do Markdown é escapado e a política de conteúdo bloqueia scripts e recursos externos.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>Extensões que a pré-visualização sabe exibir.</summary>
    public static bool CanPreview(string? relativePath) =>
        relativePath is not null
        && (relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>Só o fragmento HTML do corpo (sem frontmatter).</summary>
    public static string ToHtmlFragment(string? markdown) =>
        Markdown.ToHtml(StripFrontmatter(markdown ?? "").ReplaceLineEndings("\n"), Pipeline);

    /// <summary>Página completa, com estilo claro ou escuro, pronta para o WebView2.</summary>
    public static string ToHtmlPage(string? markdown, bool dark)
    {
        var body = ToHtmlFragment(markdown);
        if (string.IsNullOrWhiteSpace(body))
            body = "<p class=\"empty\">Nada para visualizar.</p>";
        return $$"""
            <!DOCTYPE html>
            <html lang="pt-BR" class="{{(dark ? "dark" : "light")}}">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
            <style>{{Css}}</style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    /// <summary>Remove um bloco de frontmatter YAML no início (arquivos de referência podem ter).</summary>
    public static string StripFrontmatter(string markdown)
    {
        var m = FrontmatterRegex().Match(markdown);
        return m.Success ? markdown[m.Length..] : markdown;
    }

    [GeneratedRegex(@"\A﻿?---[ \t]*\r?\n.*?\r?\n---[ \t]*(\r?\n|\z)", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();


    private const string Css = """
        :root { color-scheme: light; --fg:#1b1b1b; --muted:#5f5f5f; --bg:#ffffff; --code-bg:#f3f3f3; --border:#d9d9d9; --link:#005fb8; --quote:#8a8a8a; }
        html.dark { color-scheme: dark; --fg:#e6e6e6; --muted:#a8a8a8; --bg:#1f1f1f; --code-bg:#2b2b2b; --border:#3d3d3d; --link:#60cdff; --quote:#7a7a7a; }
        html, body { background: var(--bg); color: var(--fg); }
        body { font: 14px/1.6 "Segoe UI Variable Text", "Segoe UI", sans-serif; margin: 0; padding: 14px 20px 32px; max-width: 900px; word-wrap: break-word; }
        h1, h2, h3, h4, h5, h6 { font-family: "Segoe UI Variable Display", "Segoe UI", sans-serif; font-weight: 600; line-height: 1.25; margin: 1.4em 0 .5em; }
        h1 { font-size: 1.8em; padding-bottom: .25em; border-bottom: 1px solid var(--border); }
        h2 { font-size: 1.4em; padding-bottom: .2em; border-bottom: 1px solid var(--border); }
        h3 { font-size: 1.15em; }
        body > :first-child { margin-top: 0; }
        p, ul, ol, table, pre, blockquote { margin: 0 0 .9em; }
        ul, ol { padding-left: 1.8em; }
        li + li { margin-top: .2em; }
        a { color: var(--link); text-decoration: none; }
        a:hover { text-decoration: underline; }
        code { font-family: "Cascadia Mono", Consolas, monospace; font-size: .9em; background: var(--code-bg); padding: .1em .35em; border-radius: 4px; }
        pre { background: var(--code-bg); padding: 10px 12px; border-radius: 6px; overflow-x: auto; }
        pre code { background: none; padding: 0; font-size: .88em; }
        blockquote { border-left: 4px solid var(--border); padding: 0 1em; color: var(--muted); }
        table { border-collapse: collapse; display: block; overflow-x: auto; }
        th, td { border: 1px solid var(--border); padding: 5px 10px; text-align: left; vertical-align: top; }
        th { background: var(--code-bg); font-weight: 600; }
        hr { border: 0; border-top: 1px solid var(--border); margin: 1.5em 0; }
        input[type=checkbox] { margin-right: .4em; }
        .empty { color: var(--quote); font-style: italic; }
        """;
}
