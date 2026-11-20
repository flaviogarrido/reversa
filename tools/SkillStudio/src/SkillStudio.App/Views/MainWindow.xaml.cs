using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkillStudio.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SkillStudio.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel { ConfirmAsync = ConfirmAsync };
        DataContext = _vm;
        Loaded += (_, _) =>
        {
            ThemeHelper.Apply(_vm.Settings.Theme, this);
            // SkillStudio.App.exe <nome-da-skill> [aba] abre direto uma skill do repositório.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && _vm.Skills.FirstOrDefault(s => s.Name == args[1]) is { } skill)
            {
                _vm.OpenSkillCommand.Execute(skill);
                if (args.Length > 2 && int.TryParse(args[2], out var tab)) _vm.SelectedTabIndex = tab;
            }
        };
        _vm.ChatMessages.CollectionChanged += OnChatChanged;
        Closing += OnClosing;
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    private void OnSkillDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: SkillListItem item })
            _vm.OpenSkillCommand.Execute(item);
    }

    private void OnIssueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IssueList.SelectedItem is IssueViewModel issue)
            _vm.GoToIssueCommand.Execute(issue);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _vm.ApplySettings(dialog.Result);
            ThemeHelper.Apply(dialog.Result.Theme, this);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Descartar",
            CloseButtonText = "Cancelar",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || !_vm.IsDirty) return;
        e.Cancel = true;
        if (await ConfirmAsync("Sair sem salvar?", "A skill aberta tem alterações não salvas."))
        {
            _closeConfirmed = true;
            Close();
        }
    }
}

internal static class ThemeHelper
{
    private static bool _watching;

    /// <summary>Chamar só com a janela carregada (o watcher do WPF-UI exige).</summary>
    public static void Apply(string theme, Window window)
    {
        if (_watching)
        {
            SystemThemeWatcher.UnWatch(window);
            _watching = false;
        }
        switch (theme)
        {
            case "Light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case "Dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                SystemThemeWatcher.Watch(window);
                _watching = true;
                break;
        }
    }
}
