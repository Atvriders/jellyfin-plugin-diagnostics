# Button Press History — Design

**Date:** 2026-07-13
**Status:** Approved

## Problem

The Diagnostics dashboard has three buttons — **Run Diagnostics**, **Export Report**, and **Analyze with AI** — but the plugin keeps no record of them being used. `DiagnosticsService` holds only the most recent report in a private field (`_lastReport`), which is lost on every server restart. There is no way to answer "when was the last scan run, by whom, and what did it find?"

We want a persistent, on-disk history of every button press, viewable from the dashboard.

## Prerequisite: the build is broken

Work on this feature uncovered a pre-existing defect that blocks it.

`JellyfinDiagnostics.csproj` references `Jellyfin.Controller` with a **floating** version (`10.11.*`). Jellyfin shipped a breaking change *within* the 10.11 patch line:

| Jellyfin.Controller | `IUserManager.Users` property | `IUserManager.GetUsers()` |
|---|---|---|
| 10.11.0 | yes | no |
| 10.11.6 | yes | no |
| 10.11.11 | **removed** | yes |

Consequences:

1. **`master` no longer compiles.** The float resolves 10.11.11, where `SecurityChecker`'s three `_userManager.Users` accesses do not exist (7 compile errors). CI fails on the next push.
2. **The shipped v1.0.5 plugin is broken on current servers.** It was compiled against ≤10.11.6 and binds the `Users` property. On a 10.11.11 server, `SecurityChecker` throws at runtime. `RunAllAsync` catches per-checker exceptions, so the server does not crash — the Security category silently degrades to a "Checker failed" warning. `manifest.json` declares `targetAbi 10.11.0.0`, so 10.11.11 users can and do install it.
3. **No single compile target covers 10.11.x.** Compiling against 10.11.6 breaks on 10.11.11; compiling against 10.11.11 breaks on 10.11.6.

### Decision

- **Pin** `Jellyfin.Controller` / `Jellyfin.Model` to an explicit `10.11.11` (no more floating). Builds become reproducible and cannot silently drift again.
- **Enumerate users through reflection**, not a static call. Because we compile against 10.11.11, a static `GetUsers()` call would throw `MissingMethodException` on 10.11.0–10.11.6 servers. A small helper probes for the `GetUsers()` method and falls back to the `Users` property, so one DLL supports the whole 10.11.x line. This continues the idiom already established in commit `fe1cfce` (reflection for the `PluginStatus` enum and the User admin check).
- Audit the other checkers for further APIs that differ across 10.11.x. The compiler only protects us against the version we build against; it cannot catch a method that is missing on an *older* server.

This repair ships in the same commit as the feature, per the owner's instruction.

## Feature design

### Data model

```
enum ButtonAction { RunDiagnostics, ExportReport, AnalyzeWithAi }
enum HistoryOutcome { Success, Failed }

class HistoryEntry
    string          Id              // GUID, also the snapshot filename
    DateTime        Timestamp       // UTC, when the button was pressed
    ButtonAction    Action
    string          UserName        // admin who pressed it
    HistoryOutcome  Outcome
    long            DurationMs      // Run / AI only; 0 otherwise
    string?         ErrorMessage    // populated when Outcome == Failed
    // RunDiagnostics entries only:
    int?            CriticalCount
    int?            WarningCount
    int?            InfoCount
    string?         JellyfinVersion
    string?         OperatingSystem
    bool            HasReport       // true when a snapshot file exists
```

### Storage

A `diagnostics-history/` directory under the plugin's data path:

```
diagnostics-history/
  index.json          # JSON array of HistoryEntry (metadata only), newest last
  reports/<id>.json   # full DiagnosticsReport snapshot, one per Run press
```

Splitting the index from the snapshots is the core storage decision. The index stays small, so rendering the history table reads one small file; full reports — 11 checkers' worth of findings each — are only read when the user clicks into a specific run. Storing snapshots inline in a single file would force a multi-megabyte rewrite on *every* button press once the history fills up.

**Retention:** keep the most recent **500** entries. Trimming drops old index rows and deletes their orphaned snapshot files in the same operation.

**Durability:** all writes are serialized through a `SemaphoreSlim` and performed atomically (write to a temp file, then replace). A container killed mid-write cannot leave a half-written index.

**Corruption recovery:** an unparseable `index.json` is renamed to `index.json.corrupt` and a fresh index is started. A damaged history file must never prevent the plugin from loading.

### HistoryService

A DI-registered singleton. It takes a **plain root path string**, not `IApplicationPaths`, so it can be unit-tested against a temp directory with no Jellyfin runtime.

```
Task<HistoryEntry> RecordAsync(HistoryEntry entry, DiagnosticsReport? snapshot, CancellationToken ct)
Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int startIndex, int limit, CancellationToken ct)
Task<DiagnosticsReport?> GetReportAsync(string id, CancellationToken ct)
Task ClearAsync(CancellationToken ct)
```

### Recording each button

**Run Diagnostics** and **Analyze with AI** hit real server endpoints, so they are recorded **server-side, authoritatively** — inside the endpoint, wrapped in try/catch, capturing true outcome, duration, and (for Run) the summary counts plus the report snapshot. A failed run is still recorded, with `Outcome = Failed` and the error message.

**Export Report** is the exception: today it never touches the server. `exportReport()` builds a Blob from the in-memory report client-side. To record it we add a narrow endpoint the page calls to report the press:

```
POST Diagnostics/History/Record  { action: "ExportReport" }
```

This is the one place where the client asserts that a press happened. It is admin-gated (`RequiresElevation`, like every other endpoint here) and **validates the action against a strict allowlist server-side**, so it cannot be used to forge arbitrary entries. Rewiring Export to the existing `Diagnostics/Report/Download` endpoint was considered — it would be authoritative — but it changes the download to a server round-trip with an auth token in the URL, which is a worse trade for a cosmetic gain.

### API

All under the existing `[Authorize(Policy = "RequiresElevation")]` controller.

| Endpoint | Purpose |
|---|---|
| `GET Diagnostics/History?startIndex=&limit=` | Page of entries, newest first (metadata only) |
| `GET Diagnostics/History/{id}/Report` | Full snapshot for one Run entry; 404 if absent |
| `POST Diagnostics/History/Record` | Record a UI-only press (Export); allowlisted actions only |
| `DELETE Diagnostics/History` | Clear all history and snapshots |

### UI

A collapsible **History** section on the Diagnostics page, below the findings, following the existing `diag-category-section` visual pattern.

- Table: **Time** / **Action** / **User** / **Result**. Result shows the ❌ / ⚠️ / ✅ counts for successful runs, or a red error chip for failures.
- Run rows get a **View** button that fetches the snapshot and renders it into the main content area through the existing `renderReport()` — no duplicate rendering logic.
- A **Clear history** button, behind a confirm.
- Loads on `pageshow` and refreshes after each button press.
- Built with `textContent` only, no `innerHTML`, matching the page's existing XSS-safe style.

### Error handling

History is **observability, not core function**. A failure to write history must never fail the diagnostics run the user actually asked for: `RecordAsync` failures are caught and logged, and the endpoint still returns its report. The UI degrades to "No history yet" if the endpoint errors.

### Testing

The plugin currently has **no tests at all**. This adds an xUnit project at `Tests/JellyfinDiagnostics.Tests/`, covering the logic that can silently lose data:

- round-trip: record then read back
- trim at exactly 500; the 501st press evicts the oldest and deletes its snapshot
- snapshot written only for Run presses; `GetReportAsync` returns null for others
- corrupt `index.json` is quarantined and recovered, not fatal
- concurrent `RecordAsync` calls do not interleave or lose entries
- failed runs are recorded with the error message
- `POST Record` rejects an action outside the allowlist
- reflection-based user enumeration works against both API shapes

The test project lives in `Tests/` with **no solution file**, so the root `dotnet build` still resolves the single plugin csproj and the existing release pipeline is untouched. CI gains a `dotnet test` step.

## Out of scope

- Charting or trend analysis of history over time
- Exporting the history itself (only individual snapshots are viewable)
- Recording page views, expand/collapse, or any non-button interaction
- Retention by age (only a count cap)
