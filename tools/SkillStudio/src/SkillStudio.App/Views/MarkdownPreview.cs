using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SkillStudio.Core.Preview;
using Wpf.Ui.Appearance;

namespace SkillStudio.App.Views;

/// <summary>
/// Markdown formatado num WebView2. A página não roda scripts nem carrega nada de fora (veja
/// <see cref="MarkdownRenderer"/>); links http(s) abrem no navegador padrão. Re-renderiza com atraso
/// curto enquanto o texto é digitado e só quando está visível.
/// </summary>
public sealed class MarkdownPreview : UserControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPreview), new PropertyMetadata("", (d, _) => ((MarkdownPreview)d).ScheduleRender()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly TextBlock _fallback = new()
    {
        Margin = new Thickness(12),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Task? _init;
    private bool _pending = true;
    private bool _allowNavigation;
    private string? _lastHtml;

    public MarkdownPreview()
    {
        _fallback.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Content = new Grid { Children = { _web, _fallback } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Render(); };
        Loaded += async (_, _) => { await EnsureReadyAsync(); Render(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && _pending) Render(); };
        ApplicationThemeManager.Changed += (_, _) => Dispatcher.BeginInvoke(() => { _lastHtml = null; _pending = true; Render(); });
    }

    private void ScheduleRender()
    {
        _pending = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Render()
    {
        if (!IsVisible || _web.CoreWebView2 is null) return;
        _pending = false;
        var html = MarkdownRenderer.ToHtmlPage(Markdown, ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);
        if (html == _lastHtml) return;
        _lastHtml = html;
        _allowNavigation = true;
        _web.NavigateToString(html);
    }

    private Task EnsureReadyAsync() => _init ??= InitAsync();

    private async Task InitAsync()
    {
        try
        {
            // A pasta padrão fica ao lado do .exe, que pode não ser gravável.
            _environment ??= CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReversaSkillStudio", "WebView2"));
            await _web.EnsureCoreWebView2Async(await _environment);
            var core = _web.CoreWebView2!;
            var s = core.Settings;
            s.IsScriptEnabled = false;
            s.IsWebMessageEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            _web.Visibility = Visibility.Collapsed;
            _fallback.Text = ex is WebView2RuntimeNotFoundException
                ? "A pré-visualização precisa do Microsoft Edge WebView2 Runtime, que não foi encontrado. Instale-o em https://developer.microsoft.com/microsoft-edge/webview2/ e reabra o Skill Studio."
                : "Não foi possível iniciar a pré-visualização: " + ex.Message;
            _fallback.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Só a página gerada por <see cref="Render"/> navega; links viram o navegador padrão.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }
        e.Cancel = true;
        OpenExternal(e.Uri);
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
