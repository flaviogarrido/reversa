using System.Diagnostics;
using System.IO;
using System.Text;

namespace SkillStudio.App.Services;

public sealed record VerifyStep(string Title, bool Success, string Output);

/// <summary>
/// Roda os gates oficiais do repositório (os mesmos do CI): scripts/verify-invocation.py e
/// scripts/test-installer-transport.mjs. Força UTF-8 no Python para o console do Windows.
/// </summary>
public static class RepoVerifier
{
    public static async Task<IReadOnlyList<VerifyStep>> RunAsync(string repoRoot, CancellationToken ct = default)
    {
        var steps = new List<VerifyStep>();
        var python = await FindPythonAsync(repoRoot, ct);
        steps.Add(python is null
            ? new VerifyStep("Eixo de invocação (verify-invocation.py)", false,
                "Python não encontrado no PATH. Instale o Python 3 ou rode o verificador pelo CI.")
            : await RunAsync("Eixo de invocação (verify-invocation.py)", python, "scripts/verify-invocation.py", repoRoot, ct));

        steps.Add(await RunAsync("Transporte do instalador (test-installer-transport.mjs)", "node", "scripts/test-installer-transport.mjs", repoRoot, ct));
        return steps;
    }

    private static async Task<string?> FindPythonAsync(string root, CancellationToken ct)
    {
        foreach (var candidate in new[] { "python", "py", "python3" })
        {
            var probe = await RunAsync("probe", candidate, "--version", root, ct);
            if (probe.Success && probe.Output.Contains("Python 3", StringComparison.Ordinal)) return candidate;
        }
        return null;
    }

    private static async Task<VerifyStep> RunAsync(string title, string exe, string args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = (await stdout + await stderr).Trim();
            return new VerifyStep(title, process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return new VerifyStep(title, false, $"Não foi possível executar \"{exe}\": {ex.Message}");
        }
    }
}
