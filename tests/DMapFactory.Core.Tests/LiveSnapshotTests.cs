using DMapFactory.Acquisition;
using DMapFactory.Core.Models;

namespace DMapFactory.Core.Tests;

public sealed class LiveSnapshotTests
{
    [Fact]
    public void ApplyBatch_GroupsSameTimestamp_WhenEnabled()
    {
        var snapshot = new LiveSnapshot { GroupSameTimestamp = true };
        var t0 = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

        var samples = new Sample[]
        {
            new(1, t0, t0, TimestampOrigin.Source, 10.5, 0u),
            new(2, t0, t0, TimestampOrigin.Source, 20.5, 0u),
            new(3, t0, t0, TimestampOrigin.Source, "Running", 0u),
        };

        var rows = snapshot.ApplyBatch(samples);

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(t0, row.TimestampUtc);
        Assert.Equal(3, row.ChangedTagIds.Count);
        Assert.Equal(10.5, row.ValuesByTagId[1]);
        Assert.Equal(20.5, row.ValuesByTagId[2]);
        Assert.Equal("Running", row.ValuesByTagId[3]);
    }

    [Fact]
    public void ApplyBatch_EmitsRowPerSample_WhenGroupSameTimestampIsFalse()
    {
        var snapshot = new LiveSnapshot { GroupSameTimestamp = false };
        var t0 = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

        var samples = new Sample[]
        {
            new(1, t0, t0, TimestampOrigin.Source, 10.5, 0u),
            new(2, t0, t0, TimestampOrigin.Source, 20.5, 0u),
        };

        var rows = snapshot.ApplyBatch(samples);

        Assert.Equal(2, rows.Count);
        Assert.Equal(10.5, rows[0].ValuesByTagId[1]);
        Assert.Equal(20.5, rows[1].ValuesByTagId[2]);
    }

    [Fact]
    public void ApplyTimestampSourceToAllRows_RecalculatesTimestampsAndDeltas()
    {
        var snapshot = new LiveSnapshot { GroupSameTimestamp = true };
        var tSource = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var tServer = tSource.AddMilliseconds(50);
        var tLocal = tSource.AddMilliseconds(100);

        var sample1 = new Sample(1, tSource, tSource, TimestampOrigin.Source, 100.0, 0u)
        {
            ServerTimestampUtc = tServer,
            LocalTimestampUtc = tLocal
        };

        var tSource2 = tSource.AddSeconds(1);
        var tServer2 = tServer.AddSeconds(1);
        var tLocal2 = tLocal.AddSeconds(1);

        var sample2 = new Sample(1, tSource2, tSource2, TimestampOrigin.Source, 200.0, 0u)
        {
            ServerTimestampUtc = tServer2,
            LocalTimestampUtc = tLocal2
        };

        snapshot.ApplyBatch([sample1]);
        snapshot.ApplyBatch([sample2]);

        var rows = snapshot.GetAllRows();
        Assert.Equal(2, rows.Count);

        // Altera para ServerTimestamp
        snapshot.ApplyTimestampSourceToAllRows(TimestampSourceMode.Server);
        Assert.Equal(tServer, rows[0].TimestampUtc);
        Assert.Equal(tServer2, rows[1].TimestampUtc);
        Assert.Equal(TimeSpan.FromSeconds(1), rows[1].DeltaTime);

        // Altera para LocalTimestamp
        snapshot.ApplyTimestampSourceToAllRows(TimestampSourceMode.Local);
        Assert.Equal(tLocal, rows[0].TimestampUtc);
        Assert.Equal(tLocal2, rows[1].TimestampUtc);
        Assert.Equal(TimeSpan.FromSeconds(1), rows[1].DeltaTime);
    }

    [Fact]
    public void Marker_Insert_Update_And_Remove_MaintainsConsistency()
    {
        var snapshot = new LiveSnapshot();
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        snapshot.ApplyBatch([new Sample(1, t0, t0, TimestampOrigin.Source, 55.0, 0u)]);

        var markerRow = snapshot.InsertMarker("Marcador 1", t0.AddSeconds(5));
        Assert.NotNull(markerRow);
        Assert.True(markerRow.IsMarker);
        Assert.Equal("Marcador 1", markerRow.MarkerText);
        Assert.Empty(markerRow.ChangedTagIds); // Linha sintética
        Assert.Equal(55.0, markerRow.ValuesByTagId[1]); // Preserva último snapshot

        // Atualiza texto do marcador
        snapshot.UpdateMarkerText(markerRow.TimestampUtc, "Marcador 1", "Marcador Renomeado");
        var allRows = snapshot.GetAllRows();
        var updatedRow = allRows.First(r => r.IsMarker);
        Assert.Equal("Marcador Renomeado", updatedRow.MarkerText);

        // Remove marcador sintético (deve remover a linha da fila)
        snapshot.RemoveMarker(markerRow.TimestampUtc, "Marcador Renomeado");
        var remainingRows = snapshot.GetAllRows();
        Assert.DoesNotContain(remainingRows, r => r.IsMarker);
        Assert.Single(remainingRows);
    }

    [Fact]
    public void Reset_ClearsAllWorkingState()
    {
        var snapshot = new LiveSnapshot();
        var t0 = DateTime.UtcNow;

        snapshot.ApplyBatch([
            new Sample(1, t0, t0, TimestampOrigin.Source, true, 0u),
            new Sample(2, t0, t0, TimestampOrigin.Source, 42.0, 0u)
        ]);

        Assert.NotEmpty(snapshot.GetAllRows());
        Assert.NotEmpty(snapshot.GetLatestSamples());

        snapshot.Reset();

        Assert.Empty(snapshot.GetAllRows());
        Assert.Empty(snapshot.GetLatestSamples());
    }
}
