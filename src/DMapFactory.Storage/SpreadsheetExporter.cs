using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using DMapFactory.Core.Models;

namespace DMapFactory.Storage;

/// <summary>
/// Exportador da planilha para CSV/TXT (formato largo ou estreito) e Excel .xlsx (RF-GRD-08),
/// preservando o destaque das células alteradas (RF-GRD-02) e dos ciclos de máquina (RF-GRD-10).
/// </summary>
public static class SpreadsheetExporter
{
    public static async Task ExportWideCsvAsync(
        string filePath,
        IReadOnlyList<TagConfig> tags,
        IReadOnlyList<WideSpreadsheetRow> rows,
        char separator = ',',
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var sb = new StringBuilder(256);
        sb.Append("RowNumber").Append(separator)
          .Append("TimestampUtc").Append(separator)
          .Append("Cycle").Append(separator)
          .Append("Marker");

        foreach (var tag in tags)
        {
            sb.Append(separator).Append(EscapeCsv(tag.Alias, separator));
        }

        await sw.WriteLineAsync(sb.ToString());

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.Clear();
            sb.Append(row.RowNumber.ToString(CultureInfo.InvariantCulture)).Append(separator)
              .Append(row.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(separator)
              .Append(EscapeCsv(row.CycleDisplay, separator)).Append(separator)
              .Append(EscapeCsv(row.MarkerDisplay, separator));

            foreach (var tag in tags)
            {
                sb.Append(separator).Append(EscapeCsv(row[tag.Id], separator));
            }

            await sw.WriteLineAsync(sb.ToString());
        }
    }

    public static async Task ExportNarrowCsvAsync(
        string filePath,
        IReadOnlyList<TagConfig> tags,
        IReadOnlyList<WideSpreadsheetRow> rows,
        char separator = ',',
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var tagById = tags.ToDictionary(t => t.Id);

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await sw.WriteLineAsync(string.Join(separator, "RowNumber", "TimestampUtc", "Cycle", "TagId", "TagAlias", "Value"));

        var sb = new StringBuilder(128);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string ts = row.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            string cycle = row.CycleNumber?.ToString(CultureInfo.InvariantCulture) ?? "";

            foreach (int changedTagId in row.ChangedTagIds.OrderBy(id => id))
            {
                string alias = tagById.TryGetValue(changedTagId, out var t) ? t.Alias : $"Tag_{changedTagId}";
                sb.Clear();
                sb.Append(row.RowNumber.ToString(CultureInfo.InvariantCulture)).Append(separator)
                  .Append(ts).Append(separator)
                  .Append(cycle).Append(separator)
                  .Append(changedTagId.ToString(CultureInfo.InvariantCulture)).Append(separator)
                  .Append(EscapeCsv(alias, separator)).Append(separator)
                  .Append(EscapeCsv(row[changedTagId], separator));

                await sw.WriteLineAsync(sb.ToString());
            }
        }
    }

    public static void ExportXlsx(
        string filePath,
        IReadOnlyList<TagConfig> tags,
        IReadOnlyList<WideSpreadsheetRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Planilha");

        // Cabeçalhos
        ws.Cell(1, 1).Value = "#";
        ws.Cell(1, 2).Value = "Timestamp (UTC)";
        ws.Cell(1, 3).Value = "Cycle";
        ws.Cell(1, 4).Value = "Marker";

        for (int i = 0; i < tags.Count; i++)
        {
            ws.Cell(1, 5 + i).Value = tags[i].Alias;
        }

        var headerRange = ws.Range(1, 1, 1, 4 + tags.Count);
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E4E7EB");
        headerRange.Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);

        var cycleRowColor = XLColor.FromHtml("#E3ECF5");
        var changedCellColor = XLColor.FromHtml("#FFF4C2");

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int excelRow = r + 2;

            ws.Cell(excelRow, 1).Value = row.RowNumber;
            ws.Cell(excelRow, 2).Value = row.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            ws.Cell(excelRow, 3).Value = row.CycleDisplay;
            if (row.IsMarker)
            {
                ws.Cell(excelRow, 4).Value = row.MarkerDisplay;
                ws.Range(excelRow, 1, excelRow, 4 + tags.Count).Style.Fill.BackgroundColor = XLColor.FromArgb(0xFE, 0xF3, 0xC7);
            }
            else
            {
                ws.Cell(excelRow, 4).Value = "";
            }

            if (row.IsCycleStart)
            {
                ws.Range(excelRow, 1, excelRow, 4 + tags.Count).Style.Fill.BackgroundColor = cycleRowColor;
            }

            for (int c = 0; c < tags.Count; c++)
            {
                int tagId = tags[c].Id;
                var cell = ws.Cell(excelRow, 5 + c);

                if (row.ValuesByTagId.TryGetValue(tagId, out var rawVal) && rawVal is not null)
                {
                    switch (rawVal)
                    {
                        case bool b:
                            cell.Value = b ? 1 : 0;
                            break;
                        case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                            cell.Value = d;
                            break;
                        case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                            cell.Value = (double)f;
                            break;
                        case int iv:
                            cell.Value = iv;
                            break;
                        case long lv:
                            cell.Value = lv;
                            break;
                        default:
                            cell.Value = WideSpreadsheetRow.FormatCell(rawVal);
                            break;
                    }
                }

                if (row.IsTagChanged(tagId))
                {
                    cell.Style.Fill.BackgroundColor = changedCellColor;
                    cell.Style.Font.Bold = true;
                }
            }
        }

        // Aba de metadados das tags
        var wsMeta = wb.Worksheets.Add("Tags");
        wsMeta.Cell(1, 1).Value = "TagId";
        wsMeta.Cell(1, 2).Value = "Alias";
        wsMeta.Cell(1, 3).Value = "NodeId / Address";
        wsMeta.Cell(1, 4).Value = "DataType";
        wsMeta.Cell(1, 5).Value = "Unit";
        wsMeta.Range(1, 1, 1, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#E4E7EB");
        wsMeta.Range(1, 1, 1, 5).Style.Font.Bold = true;

        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            int rowIdx = i + 2;
            wsMeta.Cell(rowIdx, 1).Value = t.Id;
            wsMeta.Cell(rowIdx, 2).Value = t.Alias;
            wsMeta.Cell(rowIdx, 3).Value = t.Address;
            wsMeta.Cell(rowIdx, 4).Value = t.DataType ?? "";
            wsMeta.Cell(rowIdx, 5).Value = t.Unit ?? "";
        }

        wb.SaveAs(filePath);
    }

    private static string EscapeCsv(string value, char separator)
    {
        if (value.IndexOfAny([separator, '"', '\r', '\n']) < 0)
            return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
