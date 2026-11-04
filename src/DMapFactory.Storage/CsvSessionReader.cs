using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DMapFactory.Core.Models;

namespace DMapFactory.Storage;

/// <summary>Sessão carregada do disco para análise off-line (RF-STO-05).</summary>
public sealed record LoadedSession(
    string CsvFilePath,
    SessionMetadata Metadata,
    IReadOnlyList<Sample> Samples);

/// <summary>
/// Lê arquivos de sessão <c>.csv</c> e <c>.session.json</c> para análise off-line (RF-STO-05).
/// Tolera última linha truncada caso a sessão tenha sofrido queda abrupta de energia (RF-STO-04).
/// </summary>
public static class CsvSessionReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<LoadedSession> LoadAsync(string filePath, CancellationToken ct = default)
    {
        var (csvPath, jsonPath) = ResolvePaths(filePath);

        SessionMetadata metadata;
        if (File.Exists(jsonPath))
        {
            await using var js = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            metadata = await JsonSerializer.DeserializeAsync<SessionMetadata>(js, JsonOptions, ct).ConfigureAwait(false)
                       ?? new SessionMetadata();
        }
        else
        {
            metadata = new SessionMetadata { Project = Path.GetFileNameWithoutExtension(csvPath) };
        }

        var tagTypes = metadata.Tags.ToDictionary(t => t.Id, t => t.DataType, EqualityComparer<int>.Default);
        var samples = new List<Sample>(capacity: (int)Math.Clamp(metadata.TotalSamples, 256, 4_000_000));

        await using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false); // cabeçalho
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryParseLine(line, tagTypes, out var sample))
            {
                samples.Add(sample);
            }
        }

        return new LoadedSession(csvPath, metadata, samples);
    }

    internal static bool TryParseLine(
        string line,
        IReadOnlyDictionary<int, string?> tagTypes,
        out Sample sample)
    {
        return TryParseLine(line.AsSpan(), tagTypes, out sample);
    }

    internal static bool TryParseLine(
        ReadOnlySpan<char> line,
        IReadOnlyDictionary<int, string?> tagTypes,
        out Sample sample)
    {
        sample = default;
        if (line.IsEmpty) return false;

        // Campo 0: Seq
        int idx0 = line.IndexOf(';');
        if (idx0 < 0) return false;
        var seqSpan = line[..idx0];
        if (!long.TryParse(seqSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return false;

        var rem1 = line[(idx0 + 1)..];

        // Campo 1: TimestampUtc
        int idx1 = rem1.IndexOf(';');
        if (idx1 < 0) return false;
        var tsSpan = rem1[..idx1];
        if (!DateTime.TryParse(tsSpan, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime tsUtc))
            return false;

        var rem2 = rem1[(idx1 + 1)..];

        // Campo 2: TagId
        int idx2 = rem2.IndexOf(';');
        if (idx2 < 0) return false;
        var tagIdSpan = rem2[..idx2];
        if (!int.TryParse(tagIdSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tagId))
            return false;

        var rem3 = rem2[(idx2 + 1)..];

        // Campo 3: Value (pode estar entre aspas)
        ReadOnlySpan<char> valSpan;
        bool hasEscapedQuotes = false;
        ReadOnlySpan<char> rem4;

        if (!rem3.IsEmpty && rem3[0] == '"')
        {
            // Varre procurando o fechamento de aspas
            int quoteEnd = -1;
            for (int i = 1; i < rem3.Length; i++)
            {
                if (rem3[i] == '"')
                {
                    if (i + 1 < rem3.Length && rem3[i + 1] == '"')
                    {
                        hasEscapedQuotes = true;
                        i++; // pula aspas dupla escapada
                    }
                    else
                    {
                        quoteEnd = i;
                        break;
                    }
                }
            }

            if (quoteEnd < 0) return false;
            valSpan = rem3.Slice(1, quoteEnd - 1);
            int nextSemi = rem3[quoteEnd..].IndexOf(';');
            if (nextSemi < 0) return false;
            rem4 = rem3[(quoteEnd + nextSemi + 1)..];
        }
        else
        {
            int idx3 = rem3.IndexOf(';');
            if (idx3 < 0) return false;
            valSpan = rem3[..idx3];
            rem4 = rem3[(idx3 + 1)..];
        }

        // Campo 4: Quality
        int idx4 = rem4.IndexOf(';');
        ReadOnlySpan<char> qualitySpan;
        ReadOnlySpan<char> originSpan;

        if (idx4 >= 0)
        {
            qualitySpan = rem4[..idx4];
            originSpan = rem4[(idx4 + 1)..].Trim();
        }
        else
        {
            qualitySpan = rem4.Trim();
            originSpan = default;
        }

        tagTypes.TryGetValue(tagId, out string? dataType);
        object? value = ParseTypedValue(valSpan, hasEscapedQuotes, dataType);

        uint statusCode = qualitySpan.SequenceEqual("Good")
            ? 0x0000_0000u
            : qualitySpan.SequenceEqual("Uncertain")
                ? 0x4000_0000u
                : 0x8000_0000u;

        var origin = !originSpan.IsEmpty && originSpan[0] == 'S'
            ? TimestampOrigin.Source
            : !originSpan.IsEmpty && originSpan[0] == 'V'
                ? TimestampOrigin.Server
                : TimestampOrigin.Local;

        sample = new Sample(tagId, tsUtc, tsUtc, origin, value, statusCode);
        return true;
    }

    private static object? ParseTypedValue(ReadOnlySpan<char> raw, bool hasEscapedQuotes, string? dataType)
    {
        if (raw.IsEmpty) return null;

        switch (dataType)
        {
            case "Boolean":
                if (raw.SequenceEqual("1") || raw.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                if (raw.SequenceEqual("0") || raw.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                break;
            case "Int16" or "Int32" or "Int64" or "Byte" or "UInt16" or "UInt32":
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return dataType == "Int64" ? l : (int)l;
                break;
            case "Double" or "Float" or "Single":
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    return d;
                break;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return num;

        string strVal = raw.ToString();
        if (hasEscapedQuotes)
        {
            return strVal.Replace("\"\"", "\"");
        }

        return strVal;
    }

    private static (string CsvPath, string JsonPath) ResolvePaths(string filePath)
    {
        if (filePath.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase))
        {
            string basePath = filePath[..^".session.json".Length];
            return (basePath + ".csv", filePath);
        }

        if (filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            string basePath = filePath[..^".csv".Length];
            return (filePath, basePath + ".session.json");
        }

        return (filePath + ".csv", filePath + ".session.json");
    }
}
