using System.IO;
using System.Text.Json;

namespace DMapFactory.App.Services;

/// <summary>Preferências do usuário (não pertencem ao projeto).</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "pt-BR";
    public string? LastProjectPath { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string? DefaultSessionsFolder { get; set; }
    public bool VerticalColumnHeaders { get; set; }
    public double SpreadsheetHeaderHeight { get; set; }
    public bool AutoAcceptUntrustedCertificates { get; set; }
    public bool ShowMarkerColumn { get; set; } = true;
}

/// <summary>Carrega e salva <see cref="AppSettings"/> em %APPDATA%\DMapFactory\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string AppDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DMapFactory");

    public static string LogsFolder => Path.Combine(AppDataFolder, "logs");

    public static string DefaultSessionsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DMapFactory", "Sessions");

    private string FilePath => Path.Combine(AppDataFolder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception)
        {
            // Arquivo corrompido: volta ao padrão sem impedir a abertura da aplicação.
            Current = new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDataFolder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
    }
}
