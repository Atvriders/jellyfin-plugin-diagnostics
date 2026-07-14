using System.Text.Json;
using JellyfinDiagnostics.Models;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Services;

/// <summary>
/// Persists a bounded, on-disk history of dashboard button presses.
///
/// Layout (rooted at <c>rootPath</c>, normally &lt;DataPath&gt;/diagnostics-history):
///   index.json        JSON array of HistoryEntry, OLDEST FIRST
///   reports/&lt;id&gt;.json  one DiagnosticsReport snapshot per recorded run
///
/// The constructor deliberately takes a plain string rather than IApplicationPaths
/// so the whole class is unit-testable against a temp directory.
/// </summary>
public class HistoryService
{
    /// <summary>Hard retention cap. Older rows (and their snapshots) are dropped.</summary>
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _rootPath;
    private readonly string _reportsPath;
    private readonly string _indexPath;
    private readonly string _corruptIndexPath;
    private readonly string _tempIndexPath;
    private readonly ILogger<HistoryService> _logger;

    // Every read-modify-write of index.json goes through this. Concurrent presses
    // (or a press racing a Clear) would otherwise lose rows.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates the service over a directory root. The directory is created lazily on the
    /// first write, so this never throws for a path that does not exist yet.
    /// </summary>
    public HistoryService(string rootPath, ILogger<HistoryService> logger)
    {
        _rootPath = rootPath;
        _logger = logger;
        _indexPath = Path.Combine(rootPath, "index.json");
        _corruptIndexPath = _indexPath + ".corrupt";
        _tempIndexPath = _indexPath + ".tmp";
        _reportsPath = Path.Combine(rootPath, "reports");
    }

    /// <summary>
    /// Outcome of reading index.json. <see cref="Ok"/> is false ONLY when the file exists
    /// but could not be read (I/O error, sharing violation, denied ACL) - i.e. when the
    /// real history is still on disk and an empty <see cref="Entries"/> would be a lie.
    /// A missing file, and a file that was damaged and therefore quarantined, are both Ok.
    /// </summary>
    private readonly record struct IndexRead(bool Ok, List<HistoryEntry> Entries);

    /// <summary>
    /// Appends an entry to the index, optionally storing a report snapshot alongside it.
    /// Assigns Id/Timestamp when the caller left them unset, and returns the stored entry.
    /// </summary>
    public async Task<HistoryEntry> RecordAsync(HistoryEntry entry, DiagnosticsReport? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Normalise the id to canonical GUID form so that "<id>.json" is always a safe,
        // predictable file name (see GetReportAsync's traversal guard).
        entry.Id = Guid.TryParse(entry.Id, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");

        if (entry.Timestamp == default)
        {
            entry.Timestamp = DateTime.UtcNow;
        }

        entry.HasReport = snapshot != null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootPath);

            // Read BEFORE writing anything. An index we cannot read is an index we must not
            // overwrite: a transient sharing violation or NFS hiccup would otherwise be read
            // as "no history yet" and the very next write would replace 500 rows with one.
            // Recording is best-effort (the caller wraps this in RecordSafelyAsync), so
            // failing loudly here is strictly safer than destroying the file.
            var read = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            if (!read.Ok)
            {
                throw new IOException(
                    "Diagnostics history index at " + _indexPath + " exists but could not be read; refusing to overwrite it.");
            }

            var index = read.Entries;

            if (snapshot != null)
            {
                Directory.CreateDirectory(_reportsPath);
                await WriteJsonAtomicAsync(SnapshotPath(entry.Id), snapshot, cancellationToken).ConfigureAwait(false);
            }

            index.Add(entry);
            Trim(index);

            await WriteJsonAtomicAsync(_indexPath, index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return entry;
    }

    /// <summary>
    /// Returns a page of history, NEWEST FIRST (the index itself is stored oldest first).
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int startIndex, int limit, CancellationToken cancellationToken)
    {
        if (startIndex < 0)
        {
            startIndex = 0;
        }

        if (limit <= 0)
        {
            return Array.Empty<HistoryEntry>();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
            if (!read.Ok)
            {
                // Reporting "no history" for an index we simply could not read would hide a
                // real, still-present file. Surface it; the dashboard shows a load error.
                throw new IOException(
                    "Diagnostics history index at " + _indexPath + " exists but could not be read.");
            }

            var index = read.Entries;

            var page = new List<HistoryEntry>();
            for (var i = index.Count - 1 - startIndex; i >= 0 && page.Count < limit; i--)
            {
                page.Add(index[i]);
            }

            return page;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the stored snapshot for an entry, or null when the id is unknown, has no
    /// snapshot, or the snapshot file is unreadable. Never throws for bad input.
    /// </summary>
    public async Task<DiagnosticsReport?> GetReportAsync(string id, CancellationToken cancellationToken)
    {
        // Path-traversal guard: the id becomes a file name, so it must be a GUID and
        // nothing else. "../../etc/passwd" never reaches the filesystem.
        if (!Guid.TryParse(id, out var parsed))
        {
            return null;
        }

        var path = SnapshotPath(parsed.ToString("D"));
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DiagnosticsReport>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read diagnostics history snapshot {Id}", parsed);
            return null;
        }
    }

    /// <summary>
    /// Removes every entry and every stored snapshot, including the quarantined and
    /// half-written index artifacts. "Clear" is a purge the admin was promised: a
    /// leftover index.json.corrupt or index.json.tmp still holds usernames, error text
    /// and timestamps in cleartext, so those go too.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                if (Directory.Exists(_reportsPath))
                {
                    Directory.Delete(_reportsPath, recursive: true);
                }

                foreach (var path in new[] { _indexPath, _corruptIndexPath, _tempIndexPath })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not fully clear diagnostics history at {Root}", _rootPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string SnapshotPath(string id) => Path.Combine(_reportsPath, id + ".json");

    /// <summary>
    /// Reads index.json (oldest first).
    ///
    /// Three outcomes, and the caller MUST be able to tell them apart:
    ///   - the file does not exist            -> Ok, empty (genuinely no history yet)
    ///   - the file is damaged (unparseable,
    ///     or zero-length: the residue of a
    ///     torn write)                        -> quarantined to index.json.corrupt, then Ok, empty
    ///   - the file exists but cannot be read -> NOT Ok, empty
    ///
    /// The last case is the dangerous one: an antivirus/backup process holding the file,
    /// an NFS/SMB hiccup or an EMFILE burst is transient and the data is still there.
    /// Returning it as "empty history" is what lets the next write destroy 500 rows.
    /// </summary>
    private async Task<IndexRead> ReadIndexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
        {
            return new IndexRead(true, new List<HistoryEntry>());
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_indexPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read diagnostics history index at {Path}", _indexPath);
            return new IndexRead(false, new List<HistoryEntry>());
        }

        if (bytes.Length == 0)
        {
            // A zero-byte index.json is not an empty history - it is exactly what a torn
            // write leaves behind. Preserve it rather than silently writing over it.
            _logger.LogWarning("Diagnostics history index at {Path} is zero-length; quarantining it", _indexPath);
            QuarantineIndex();
            return new IndexRead(true, new List<HistoryEntry>());
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(bytes, JsonOptions);
            if (entries != null)
            {
                return new IndexRead(true, entries);
            }

            _logger.LogWarning("Diagnostics history index at {Path} deserialized to null; quarantining it", _indexPath);
            QuarantineIndex();
            return new IndexRead(true, new List<HistoryEntry>());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Diagnostics history index at {Path} is corrupt; quarantining it", _indexPath);
            QuarantineIndex();
            return new IndexRead(true, new List<HistoryEntry>());
        }
    }

    private void QuarantineIndex()
    {
        try
        {
            File.Move(_indexPath, _corruptIndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not quarantine corrupt diagnostics history index at {Path}", _indexPath);
        }
    }

    /// <summary>Drops the oldest rows past MaxEntries, deleting their snapshot files.</summary>
    private void Trim(List<HistoryEntry> index)
    {
        var excess = index.Count - MaxEntries;
        if (excess <= 0)
        {
            return;
        }

        for (var i = 0; i < excess; i++)
        {
            DeleteSnapshot(index[i].Id);
        }

        index.RemoveRange(0, excess);
    }

    private void DeleteSnapshot(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
        {
            return;
        }

        try
        {
            var path = SnapshotPath(parsed.ToString("D"));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete orphaned diagnostics history snapshot {Id}", parsed);
        }
    }

    /// <summary>
    /// Writes JSON via a temp file plus an atomic rename. A container killed mid-write
    /// leaves the previous, complete file in place rather than a truncated one.
    /// </summary>
    private static async Task WriteJsonAtomicAsync<T>(string destination, T value, CancellationToken cancellationToken)
    {
        var tempPath = destination + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, destination, overwrite: true);
    }
}
