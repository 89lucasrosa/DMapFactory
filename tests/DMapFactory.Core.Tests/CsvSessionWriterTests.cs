using DMapFactory.Core.Models;
using DMapFactory.Storage;

namespace DMapFactory.Core.Tests;

public sealed class CsvSessionWriterTests
{
    [Fact]
    public async Task ModifyMetadataAsync_ConcurrentCalls_AreThreadSafeAndPersisted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Writer", Guid.NewGuid().ToString("N"));

        try
        {
            var meta = new SessionMetadata
            {
                Project = "TestConcurrentMetadata",
                StartedUtc = DateTime.UtcNow,
                Connections = [new(1, "PLC", "opc.tcp://127.0.0.1:4840", "None/None")]
            };

            await using var writer = await CsvSessionWriter.CreateAsync(tempDir, meta);

            const int taskCount = 20;
            var tasks = new Task[taskCount];

            for (int i = 0; i < taskCount; i++)
            {
                int markerIdx = i;
                tasks[i] = Task.Run(async () =>
                {
                    await writer.ModifyMetadataAsync(m =>
                    {
                        m.Markers.Add(new SessionMarker(DateTime.UtcNow.AddMilliseconds(markerIdx * 10), $"Marker #{markerIdx}"));
                    });
                });
            }

            await Task.WhenAll(tasks);

            // Lê o arquivo de metadados gravado em disco
            var loaded = await CsvSessionReader.LoadAsync(writer.CsvFilePath);
            Assert.Equal(taskCount, loaded.Metadata.Markers.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsync_SavesMetadataWithoutObjectDisposedException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DMapFactory.Tests.Dispose", Guid.NewGuid().ToString("N"));

        try
        {
            var meta = new SessionMetadata
            {
                Project = "TestDispose",
                StartedUtc = DateTime.UtcNow
            };

            var writer = await CsvSessionWriter.CreateAsync(tempDir, meta);
            await writer.WriteBatchAsync([new Sample(1, DateTime.UtcNow, DateTime.UtcNow, TimestampOrigin.Source, 123, 0u)]);

            // Não deve lançar ObjectDisposedException nem IOException
            await writer.DisposeAsync();

            var loaded = await CsvSessionReader.LoadAsync(writer.CsvFilePath);
            Assert.NotNull(loaded.Metadata.EndedUtc);
            Assert.Equal(1, loaded.Metadata.TotalSamples);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
