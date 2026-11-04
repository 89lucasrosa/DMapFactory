using DMapFactory.Acquisition;
using DMapFactory.Core.Models;
using DMapFactory.Storage;

namespace DMapFactory.Core.Tests;

public sealed class AcquisitionAndStorageTests
{
    [Fact]
    public async Task CsvSessionWriterAndReader_RoundTripsSamplesMetadataCyclesAndMarkers()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Session", Guid.NewGuid().ToString("N"));

        try
        {
            var meta = new SessionMetadata
            {
                Project = "Linha_Teste",
                StartedUtc = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc),
                Connections = [new SessionConnectionSummary(1, "PLC1", "opc.tcp://localhost:48400", "None/None")],
                Tags =
                [
                    new SessionTagSummary(1, 1, "ns=2;s=Cycle_Active", "Cycle_Active", "Boolean", null),
                    new SessionTagSummary(2, 1, "ns=2;s=Tag_001", "Tag_001", "Double", "bar"),
                    new SessionTagSummary(3, 1, "ns=2;s=Msg", "Msg", "String", null),
                ]
            };

            DateTime t0 = meta.StartedUtc;
            Sample[] batch =
            [
                new(1, t0, t0, TimestampOrigin.Source, true, 0x0000_0000u),
                new(2, t0, t0, TimestampOrigin.Source, 12.345, 0x0000_0000u),
                new(3, t0.AddMilliseconds(100), t0.AddMilliseconds(100), TimestampOrigin.Source, "Texto;com \"aspas\"", 0x0000_0000u),
                new(1, t0.AddMilliseconds(500), t0.AddMilliseconds(500), TimestampOrigin.Source, false, 0x0000_0000u),
            ];

            string csvPath;
            await using (var writer = await CsvSessionWriter.CreateAsync(tempDir, meta))
            {
                csvPath = writer.CsvFilePath;
                await writer.WriteBatchAsync(batch);
                writer.Metadata.Markers.Add(new SessionMarker(t0.AddMilliseconds(250), "Troca de ferramenta"));
            }

            var loaded = await CsvSessionReader.LoadAsync(csvPath);

            Assert.Equal("Linha_Teste", loaded.Metadata.Project);
            Assert.Equal(4, loaded.Metadata.TotalSamples);
            Assert.Single(loaded.Metadata.Markers);
            Assert.Equal(4, loaded.Samples.Count);

            Assert.Equal(true, loaded.Samples[0].Value);
            Assert.Equal(12.345, (double)loaded.Samples[1].Value!, precision: 4);
            Assert.Equal("Texto;com \"aspas\"", loaded.Samples[2].Value);
            Assert.Equal(false, loaded.Samples[3].Value);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LiveSnapshot_GroupsSameTimestamp_And_DetectsMachineCycles()
    {
        var snapshot = new LiveSnapshot
        {
            GroupSameTimestamp = true,
            CycleTriggerTagId = 1
        };

        DateTime t0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        DateTime t1 = t0.AddMilliseconds(100);
        DateTime t2 = t0.AddMilliseconds(400);

        Sample[] samples =
        [
            // Mesmo timestamp t0: devem ser agrupados em uma única linha larga
            new(1, t0, t0, TimestampOrigin.Source, true, 0u),
            new(2, t0, t0, TimestampOrigin.Source, 100.0, 0u),
            // Timestamp t1: segunda linha mantendo Tag 1 = true e atualizando Tag 2 = 150.0
            new(2, t1, t1, TimestampOrigin.Source, 150.0, 0u),
            // Timestamp t2: fim do ciclo (Tag 1 -> false)
            new(1, t2, t2, TimestampOrigin.Source, false, 0u),
        ];

        var rows = snapshot.ApplyBatch(samples);

        Assert.Equal(3, rows.Count);

        // Linha 1 (t0): início do ciclo #1, mudou tags 1 e 2
        Assert.Equal(1, rows[0].CycleNumber);
        Assert.True(rows[0].IsCycleStart);
        Assert.Contains(1, rows[0].ChangedTagIds);
        Assert.Contains(2, rows[0].ChangedTagIds);
        Assert.Equal(100.0, rows[0].ValuesByTagId[2]);

        // Linha 2 (t1): dentro do ciclo #1, preserva Tag 1 = true e atualiza Tag 2 = 150.0
        Assert.Equal(1, rows[1].CycleNumber);
        Assert.False(rows[1].IsCycleStart);
        Assert.Equal(true, rows[1].ValuesByTagId[1]);
        Assert.Equal(150.0, rows[1].ValuesByTagId[2]);

        // Ciclo completo registrado com duração de 400 ms
        var cycle = Assert.Single(snapshot.GetCycles());
        Assert.Equal(1, cycle.Number);
        Assert.Equal(400.0, cycle.DurationMs, precision: 1);
    }

    [Fact]
    public async Task ProjectFileStore_SavesAndLoadsProjectConfig()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"dmap_{Guid.NewGuid():N}.dmproj");

        try
        {
            var project = new ProjectConfig
            {
                Name = "Prensa_01",
                Connections =
                [
                    new ConnectionConfig
                    {
                        Id = 1,
                        Name = "S7_1500",
                        EndpointUrl = "opc.tcp://192.168.0.10:4840",
                        SecurityMode = MessageSecurity.SignAndEncrypt
                    }
                ],
                Groups =
                [
                    new AcquisitionGroup
                    {
                        Name = "Ciclo_100ms",
                        ConnectionId = 1,
                        SamplingIntervalMs = 100,
                        Tags = [new TagConfig { Id = 10, ConnectionId = 1, Address = "ns=2;s=Tag_001", Alias = "Pressao" }]
                    }
                ]
            };

            await ProjectFileStore.SaveAsync(project, tempFile);
            var loaded = await ProjectFileStore.LoadAsync(tempFile);

            Assert.Equal("Prensa_01", loaded.Name);
            Assert.Single(loaded.Connections);
            Assert.Equal(MessageSecurity.SignAndEncrypt, loaded.Connections[0].SecurityMode);
            var tag = Assert.Single(loaded.AllTags);
            Assert.Equal("Pressao", tag.Alias);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SpreadsheetExporter_ExportsWideCsv_NarrowCsv_And_Xlsx()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Export", Guid.NewGuid().ToString("N"));

        try
        {
            TagConfig[] tags =
            [
                new() { Id = 1, ConnectionId = 1, Address = "ns=2;s=Cycle_Active", Alias = "Cycle_Active", DataType = "Boolean" },
                new() { Id = 2, ConnectionId = 1, Address = "ns=2;s=Tag_001", Alias = "Pressao", DataType = "Double", Unit = "bar" }
            ];

            var snapshot = new LiveSnapshot { GroupSameTimestamp = true, CycleTriggerTagId = 1 };
            DateTime t0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
            var rows = snapshot.ApplyBatch(
            [
                new(1, t0, t0, TimestampOrigin.Source, true, 0u),
                new(2, t0, t0, TimestampOrigin.Source, 42.5, 0u),
                new(2, t0.AddMilliseconds(100), t0.AddMilliseconds(100), TimestampOrigin.Source, 45.0, 0u)
            ]);

            string wideCsv = Path.Combine(tempDir, "wide.csv");
            string narrowCsv = Path.Combine(tempDir, "narrow.csv");
            string xlsxPath = Path.Combine(tempDir, "export.xlsx");

            await SpreadsheetExporter.ExportWideCsvAsync(wideCsv, tags, rows);
            await SpreadsheetExporter.ExportNarrowCsvAsync(narrowCsv, tags, rows);
            SpreadsheetExporter.ExportXlsx(xlsxPath, tags, rows);

            var wideLines = await File.ReadAllLinesAsync(wideCsv);
            Assert.Equal(3, wideLines.Length); // 1 header + 2 rows
            Assert.Contains("Cycle_Active,Pressao", wideLines[0]);
            Assert.Contains(",1,42.5", wideLines[1]);

            var narrowLines = await File.ReadAllLinesAsync(narrowCsv);
            Assert.Equal(4, narrowLines.Length); // 1 header + 2 changed in row 1 + 1 changed in row 2

            Assert.True(File.Exists(xlsxPath));
            Assert.True(new FileInfo(xlsxPath).Length > 1024);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SessionManager_AddMarker_RecordsMarkerInSessionJson()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Marker", Guid.NewGuid().ToString("N"));

        try
        {
            var sm = new DMapFactory.Acquisition.SessionManager();
            var project = new ProjectConfig
            {
                Name = "TesteMarcador",
                Connections = [new() { Id = 1, Name = "C1", EndpointUrl = "opc.tcp://127.0.0.1:4840" }]
            };

            await sm.StartSessionAsync(project, drivers: [], tempDir, "1.0.0");
            Assert.True(sm.IsOnline);

            await sm.AddMarkerAsync("Falha na esteira #2");
            await sm.AddMarkerAsync("Rearme pelo operador");

            await sm.StopSessionAsync();
            Assert.False(sm.IsOnline);

            string? jsonFile = Directory.EnumerateFiles(tempDir, "*.session.json").FirstOrDefault();
            Assert.NotNull(jsonFile);

            var loaded = await CsvSessionReader.LoadAsync(jsonFile.Replace(".session.json", ".csv"));
            Assert.Equal(2, loaded.Metadata.Markers.Count);
            Assert.Equal("Falha na esteira #2", loaded.Metadata.Markers[0].Text);
            Assert.Equal("Rearme pelo operador", loaded.Metadata.Markers[1].Text);
            Assert.True(loaded.Metadata.Markers[0].TimestampUtc <= DateTime.UtcNow);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
