using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DMapFactory.Core.Models;

namespace DMapFactory.Storage;

/// <summary>
/// Gravador de sessão em formato CSV estreito + metadados JSON (RF-STO-01..04).
/// Realiza escrita bufferizada e <see cref="FlushAsync"/> periódico para proteção contra perda em queda de energia.
/// </summary>
public sealed class CsvSessionWriter : IAsyncDisposable
{
    public const string HeaderLine = "Seq;TimestampUtc;TagId;Value;Quality;TsOrigin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly FileStream _csvStream;
    private readonly StreamWriter _csvWriter;
    private readonly StringBuilder _batchBuilder = new(4096);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _seq;
    private bool _disposed;

    private CsvSessionWriter(string csvFilePath, string jsonFilePath, SessionMetadata metadata)
    {
        CsvFilePath = csvFilePath;
        JsonFilePath = jsonFilePath;
        Metadata = metadata;

        _csvStream = new FileStream(
            csvFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        _csvWriter = new StreamWriter(_csvStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024);
    }

    public string CsvFilePath { get; }
    public string JsonFilePath { get; }
    public SessionMetadata Metadata { get; }
    public long WrittenCount => Interlocked.Read(ref _seq);

    /// <summary>
    /// Cria o par de arquivos <c>AAAA-MM-DD_HHMMSS_&lt;projeto&gt;.csv</c> e <c>.session.json</c> (RF-STO-01/02).
    /// </summary>
    public static async Task<CsvSessionWriter> CreateAsync(
        string folderPath,
        SessionMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Directory.CreateDirectory(folderPath);

        string safeProject = SanitizeFileName(string.IsNullOrWhiteSpace(metadata.Project) ? "Session" : metadata.Project);
        string stamp = metadata.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string baseName = $"{stamp}_{safeProject}";

        string csvPath = Path.Combine(folderPath, $"{baseName}.csv");
        int suffix = 1;
        while (File.Exists(csvPath))
        {
            baseName = $"{stamp}_{safeProject}_{suffix++}";
            csvPath = Path.Combine(folderPath, $"{baseName}.csv");
        }

        string jsonPath = Path.Combine(folderPath, $"{baseName}.session.json");
        var writer = new CsvSessionWriter(csvPath, jsonPath, metadata);

        await writer._csvWriter.WriteLineAsync(HeaderLine.AsMemory(), ct).ConfigureAwait(false);
        await writer._csvWriter.FlushAsync(ct).ConfigureAwait(false);
        await writer.SaveMetadataAsync(ct).ConfigureAwait(false);

        return writer;
    }

    /// <summary>Grava um lote de amostras e executa flush em disco (RF-STO-04).</summary>
    public async Task WriteBatchAsync(IReadOnlyList<Sample> samples, CancellationToken ct = default)
    {
        if (samples.Count == 0) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _batchBuilder.Clear();
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                long seq = Interlocked.Increment(ref _seq);
                AppendCsvLine(_batchBuilder, seq, in s);
            }

            foreach (var chunk in _batchBuilder.GetChunks())
            {
                await _csvWriter.WriteAsync(chunk, ct).ConfigureAwait(false);
            }
            await _csvWriter.FlushAsync(ct).ConfigureAwait(false);
            Metadata.TotalSamples = Interlocked.Read(ref _seq);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveMetadataAsync(CancellationToken ct = default)
    {
        Metadata.TotalSamples = Interlocked.Read(ref _seq);
        string tempPath = JsonFilePath + ".tmp";
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, Metadata, JsonOptions, ct).ConfigureAwait(false);
        }
        File.Move(tempPath, JsonFilePath, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Metadata.EndedUtc ??= DateTime.UtcNow;
        await _csvWriter.FlushAsync().ConfigureAwait(false);
        await _csvWriter.DisposeAsync().ConfigureAwait(false);
        await _csvStream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        await SaveMetadataAsync().ConfigureAwait(false);
    }

    internal static void AppendCsvLine(StringBuilder sb, long seq, in Sample s)
    {
        sb.Append(seq.ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append(s.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(';');
        sb.Append(s.TagId.ToString(CultureInfo.InvariantCulture)).Append(';');
        sb.Append(EncodeValue(s.Value)).Append(';');
        sb.Append(s.Quality switch
        {
            QualityClass.Good => "Good",
            QualityClass.Uncertain => "Uncertain",
            _ => "Bad"
        }).Append(';');
        sb.Append(s.Origin switch
        {
            TimestampOrigin.Source => 'S',
            TimestampOrigin.Server => 'V',
            _ => 'L'
        }).AppendLine();
    }

    internal static string EncodeValue(object? value)
    {
        if (value is null) return "";

        string raw = value switch
        {
            bool b => b ? "1" : "0",
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
            Array arr => JsonSerializer.Serialize(arr),
            _ => value.ToString() ?? ""
        };

        if (raw.IndexOfAny([';', '"', '\r', '\n']) >= 0)
        {
            return "\"" + raw.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return raw;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
        }
        return sb.ToString();
    }
}
