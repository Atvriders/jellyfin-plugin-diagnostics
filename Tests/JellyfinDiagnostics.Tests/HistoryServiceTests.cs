using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyfinDiagnostics.Tests;

public class HistoryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly HistoryService _sut;

    public HistoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jfdiag-tests-" + Guid.NewGuid().ToString("N"));
        _sut = new HistoryService(_root, NullLogger<HistoryService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static HistoryEntry Entry(ButtonAction action = ButtonAction.ExportReport) => new()
    {
        Action = action,
        UserName = "admin",
        Outcome = HistoryOutcome.Success
    };

    private static DiagnosticsReport Report() => new()
    {
        Timestamp = DateTime.UtcNow,
        JellyfinVersion = "10.11.11",
        OperatingSystem = "Linux",
        Results = new List<DiagnosticResult>
        {
            new() { Severity = DiagnosticSeverity.Critical, Title = "boom" }
        }
    };

    [Fact]
    public async Task RecordAsync_AssignsIdAndTimestamp()
    {
        var saved = await _sut.RecordAsync(Entry(), null, default);

        Assert.True(Guid.TryParse(saved.Id, out _));
        Assert.NotEqual(default, saved.Timestamp);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsNewestFirst()
    {
        var first = await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), null, default);
        var second = await _sut.RecordAsync(Entry(ButtonAction.ExportReport), null, default);

        var history = await _sut.GetHistoryAsync(0, 50, default);

        Assert.Equal(second.Id, history[0].Id);
        Assert.Equal(first.Id, history[1].Id);
    }

    [Fact]
    public async Task RecordAsync_PersistsAcrossInstances()
    {
        await _sut.RecordAsync(Entry(), null, default);

        var reloaded = new HistoryService(_root, NullLogger<HistoryService>.Instance);
        Assert.Single(await reloaded.GetHistoryAsync(0, 50, default));
    }

    [Fact]
    public async Task RecordAsync_WritesSnapshot_OnlyWhenReportGiven()
    {
        var withReport = await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), Report(), default);
        var withoutReport = await _sut.RecordAsync(Entry(ButtonAction.ExportReport), null, default);

        Assert.True(withReport.HasReport);
        Assert.False(withoutReport.HasReport);
        Assert.NotNull(await _sut.GetReportAsync(withReport.Id, default));
        Assert.Null(await _sut.GetReportAsync(withoutReport.Id, default));
    }

    [Fact]
    public async Task GetReportAsync_RoundTripsFindings()
    {
        var saved = await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), Report(), default);

        var report = await _sut.GetReportAsync(saved.Id, default);

        Assert.Equal("10.11.11", report!.JellyfinVersion);
        Assert.Equal("boom", report.Results[0].Title);
    }

    [Fact]
    public async Task RecordAsync_TrimsToMaxEntries_AndDeletesOrphanedSnapshots()
    {
        var firstId = string.Empty;
        for (var i = 0; i < HistoryService.MaxEntries + 1; i++)
        {
            var saved = await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), Report(), default);
            if (i == 0)
            {
                firstId = saved.Id;
            }
        }

        var history = await _sut.GetHistoryAsync(0, 1000, default);

        Assert.Equal(HistoryService.MaxEntries, history.Count);
        Assert.DoesNotContain(history, e => e.Id == firstId);
        Assert.Null(await _sut.GetReportAsync(firstId, default));
        Assert.False(File.Exists(Path.Combine(_root, "reports", firstId + ".json")));
    }

    [Fact]
    public async Task GetHistoryAsync_Paginates()
    {
        for (var i = 0; i < 5; i++)
        {
            await _sut.RecordAsync(Entry(), null, default);
        }

        var page = await _sut.GetHistoryAsync(1, 2, default);

        Assert.Equal(2, page.Count);
    }

    [Fact]
    public async Task CorruptIndex_IsQuarantinedAndRecovered()
    {
        await _sut.RecordAsync(Entry(), null, default);
        await File.WriteAllTextAsync(Path.Combine(_root, "index.json"), "{ this is not json");

        var recovered = new HistoryService(_root, NullLogger<HistoryService>.Instance);

        Assert.Empty(await recovered.GetHistoryAsync(0, 50, default));
        Assert.True(File.Exists(Path.Combine(_root, "index.json.corrupt")));

        // and it still records after recovery
        await recovered.RecordAsync(Entry(), null, default);
        Assert.Single(await recovered.GetHistoryAsync(0, 50, default));
    }

    [Fact]
    public async Task ConcurrentRecords_AreNotLost()
    {
        var writes = Enumerable.Range(0, 50)
            .Select(_ => _sut.RecordAsync(Entry(), null, default));
        await Task.WhenAll(writes);

        Assert.Equal(50, (await _sut.GetHistoryAsync(0, 1000, default)).Count);
    }

    [Fact]
    public async Task RecordAsync_StoresFailureDetail()
    {
        var failed = Entry(ButtonAction.RunDiagnostics);
        failed.Outcome = HistoryOutcome.Failed;
        failed.ErrorMessage = "checker exploded";

        await _sut.RecordAsync(failed, null, default);
        var history = await _sut.GetHistoryAsync(0, 1, default);

        Assert.Equal(HistoryOutcome.Failed, history[0].Outcome);
        Assert.Equal("checker exploded", history[0].ErrorMessage);
    }

    [Fact]
    public async Task GetReportAsync_ReturnsNull_ForNonGuidId()
    {
        Assert.Null(await _sut.GetReportAsync("../../etc/passwd", default));
    }

    [Fact]
    public async Task ClearAsync_RemovesEntriesAndSnapshots()
    {
        var saved = await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), Report(), default);

        await _sut.ClearAsync(default);

        Assert.Empty(await _sut.GetHistoryAsync(0, 50, default));
        Assert.Null(await _sut.GetReportAsync(saved.Id, default));
        Assert.False(File.Exists(Path.Combine(_root, "reports", saved.Id + ".json")));
    }

    /// <summary>
    /// A transient read failure (antivirus/backup holding the file, an NFS/SMB hiccup, an
    /// EMFILE burst) must NOT be mistaken for "no history yet". Treating it as an empty
    /// index means the very next record atomically renames a 1-row index over the real
    /// 500-row one and every snapshot is orphaned. The data is still on disk - so refuse
    /// to write rather than destroy it.
    /// </summary>
    [Fact]
    public async Task RecordAsync_DoesNotOverwriteIndex_WhenItExistsButCannotBeRead()
    {
        await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), null, default);
        await _sut.RecordAsync(Entry(ButtonAction.ExportReport), null, default);

        var indexPath = Path.Combine(_root, "index.json");
        var before = await File.ReadAllBytesAsync(indexPath);

        // FileShare.None reproduces the sharing violation: the read throws IOException,
        // while File.Move(tmp, index.json, overwrite: true) would still happily succeed.
        using (new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(
                () => _sut.RecordAsync(Entry(ButtonAction.ExportReport), null, default));

            await Assert.ThrowsAsync<IOException>(
                () => _sut.GetHistoryAsync(0, 50, default));
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(2, (await _sut.GetHistoryAsync(0, 50, default)).Count);
    }

    /// <summary>
    /// A zero-length index.json is not an empty history - it is the residue a torn write
    /// leaves behind. Quarantine it like any other damaged index instead of silently
    /// writing over it.
    /// </summary>
    [Fact]
    public async Task ZeroLengthIndex_IsQuarantined_NotTreatedAsEmptyHistory()
    {
        await _sut.RecordAsync(Entry(), null, default);
        await File.WriteAllBytesAsync(Path.Combine(_root, "index.json"), Array.Empty<byte>());

        var recovered = new HistoryService(_root, NullLogger<HistoryService>.Instance);
        await recovered.RecordAsync(Entry(), null, default);

        Assert.True(File.Exists(Path.Combine(_root, "index.json.corrupt")));
        Assert.Single(await recovered.GetHistoryAsync(0, 50, default));
    }

    /// <summary>
    /// "Clear the entire history? This cannot be undone." must actually purge it. The
    /// quarantine and temp files are full HistoryEntry rows - usernames, error strings,
    /// timestamps - sitting next to index.json, and they were surviving the purge.
    /// </summary>
    [Fact]
    public async Task ClearAsync_RemovesQuarantinedAndTemporaryIndexFiles()
    {
        await _sut.RecordAsync(Entry(ButtonAction.RunDiagnostics), Report(), default);

        var corruptPath = Path.Combine(_root, "index.json.corrupt");
        var tempPath = Path.Combine(_root, "index.json.tmp");
        await File.WriteAllTextAsync(corruptPath, "[{\"UserName\":\"admin\",\"ErrorMessage\":\"secret\"}]");
        await File.WriteAllTextAsync(tempPath, "[{\"UserName\":\"admin\"}]");

        await _sut.ClearAsync(default);

        Assert.False(File.Exists(corruptPath));
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(Path.Combine(_root, "index.json")));
        Assert.Empty(await _sut.GetHistoryAsync(0, 50, default));
    }
}
