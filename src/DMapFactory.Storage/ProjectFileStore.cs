using System.Text.Json;
using System.Text.Json.Serialization;
using DMapFactory.Core.Models;

namespace DMapFactory.Storage;

/// <summary>
/// Salva e carrega arquivos de projeto <c>.dmproj</c> em JSON (RF-PRJ-01).
/// Senhas permanecem criptografadas via DPAPI (<see cref="ConnectionConfig.ProtectedPassword"/>).
/// </summary>
public static class ProjectFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync(ProjectConfig project, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, project, JsonOptions, ct).ConfigureAwait(false);
    }

    public static async Task<ProjectConfig> LoadAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var project = await JsonSerializer.DeserializeAsync<ProjectConfig>(stream, JsonOptions, ct).ConfigureAwait(false);
        return project ?? new ProjectConfig();
    }
}
