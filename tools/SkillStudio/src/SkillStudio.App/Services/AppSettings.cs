using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkillStudio.App.Services;

/// <summary>
/// Preferências do usuário em %APPDATA%\SkillStudio\settings.json.
/// A chave da API, quando informada no app, é cifrada com DPAPI (só o usuário do Windows a decifra).
/// </summary>
public sealed class AppSettings
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkillStudio");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly byte[] Entropy = "SkillStudio.Reversa.v1"u8.ToArray();

    public string RepositoryPath { get; set; } = "";
    public string PreferredPrefix { get; set; } = "fg-";
    public string Author { get; set; } = "";
    public string Model { get; set; } = Core.Assistant.AssistantOptions.DefaultModel;
    public string Effort { get; set; } = "medium";
    public bool EnableWebSearch { get; set; } = true;
    public string Theme { get; set; } = "System";
    public string? ProtectedApiKey { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Arquivo corrompido: volta ao padrão sem derrubar o app.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(ProtectedApiKey)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void SetApiKey(string? key)
    {
        ProtectedApiKey = string.IsNullOrWhiteSpace(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>De onde virá a credencial, para mostrar na tela (sem revelar o valor).</summary>
    public string DescribeCredentialSource()
    {
        if (GetApiKey() is not null) return "chave salva no app (cifrada com DPAPI)";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return "variável ANTHROPIC_API_KEY";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"))) return "variável ANTHROPIC_AUTH_TOKEN";
        return "perfil do `ant auth login`, se houver";
    }
}
