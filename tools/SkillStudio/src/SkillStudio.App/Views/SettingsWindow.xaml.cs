using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SkillStudio.App.Services;
using SkillStudio.Core.IO;
using Wpf.Ui.Controls;

namespace SkillStudio.App.Views;

public partial class SettingsWindow : FluentWindow
{
    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        // Trabalha numa cópia: Cancelar não altera nada.
        Result = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current))!;
        RepoBox.Text = Result.RepositoryPath;
        PrefixBox.Text = Result.PreferredPrefix;
        AuthorBox.Text = Result.Author;
        ModelBox.Text = Result.Model;
        Select(EffortBox, Result.Effort);
        Select(ThemeBox, Result.Theme);
        WebSearchBox.IsChecked = Result.EnableWebSearch;
        CredentialHint.Text = $"Credencial em uso hoje: {Result.DescribeCredentialSource()}.";
    }

    public AppSettings Result { get; }

    private static void Select(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Content == value) box.SelectedItem = item;
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Raiz do checkout do Reversa" };
        if (dialog.ShowDialog() == true) RepoBox.Text = SkillRepository.FindRoot(dialog.FolderName) ?? dialog.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var repo = RepoBox.Text.Trim();
        if (repo.Length > 0 && !SkillRepository.IsReversaRoot(repo))
        {
            System.Windows.MessageBox.Show(this, "A pasta informada não é um checkout do Reversa (precisa de package.json com o bin \"reversa\" e da pasta agents/).",
                "Repositório inválido", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var prefix = PrefixBox.Text.Trim();
        if (prefix.Length > 0 && !prefix.EndsWith('-')) prefix += "-";

        Result.RepositoryPath = repo;
        Result.PreferredPrefix = prefix.ToLowerInvariant();
        Result.Author = AuthorBox.Text.Trim();
        Result.Model = string.IsNullOrWhiteSpace(ModelBox.Text) ? Core.Assistant.AssistantOptions.DefaultModel : ModelBox.Text.Trim();
        Result.Effort = (EffortBox.SelectedItem as ComboBoxItem)?.Content as string ?? "medium";
        Result.Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "System";
        Result.EnableWebSearch = WebSearchBox.IsChecked == true;
        if (ClearKeyBox.IsChecked == true) Result.SetApiKey(null);
        else if (!string.IsNullOrWhiteSpace(KeyBox.Password)) Result.SetApiKey(KeyBox.Password);

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
