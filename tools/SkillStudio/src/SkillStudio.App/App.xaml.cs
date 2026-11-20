using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SkillStudio.App;

public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkillStudio", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var root = e.Exception;
        while (root.InnerException is not null) root = root.InnerException;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch (IOException)
        {
            // Sem log não impede de mostrar o erro.
        }
        MessageBox.Show($"{root.Message}\n\nDetalhes em {LogPath}", "Skill Studio: erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
