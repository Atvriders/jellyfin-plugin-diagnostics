# Button Press History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist an on-disk history of every Diagnostics dashboard button press (Run / Export / AI), viewable from the dashboard — and repair the pre-existing broken build that blocks it.

**Architecture:** A `HistoryService` singleton owns a `diagnostics-history/` directory containing a small `index.json` (metadata rows) plus one `reports/<id>.json` snapshot per diagnostics run. Run and AI presses are recorded server-side inside their existing endpoints; the client-only Export press is reported through a narrow allowlisted endpoint. A new History section on the dashboard reads it back.

**Tech Stack:** C# / .NET 9, Jellyfin plugin (Jellyfin.Controller 10.11.11), `System.Text.Json`, xUnit, vanilla JS in an embedded HTML page.

## Global Constraints

- **Local SDK:** .NET 9 is at `/home/kasm-user/dotnet9`. Every build/test command must first `export PATH="/home/kasm-user/dotnet9:$PATH"`.
- **Package pin:** `Jellyfin.Controller` and `Jellyfin.Model` are pinned to exactly `10.11.11`. Never restore a floating `10.11.*`.
- **Runtime compatibility:** the single shipped DLL must work on Jellyfin **10.11.0 through 10.11.11**. `IUserManager.Users` exists only on ≤10.11.6; `IUserManager.GetUsers()` exists only on ≥10.11.11. Any user enumeration MUST go through reflection with fallback. A static call to either one is a bug.
- **Auth:** every endpoint stays under the controller's existing `[Authorize(Policy = "RequiresElevation")]`.
- **XSS:** dashboard JS uses `textContent` only. `innerHTML` is forbidden.
- **History is non-critical:** a history write failure must never fail the user's diagnostics run. Catch, log, continue.
- **Retention:** exactly 500 entries max.
- **ONE commit** at the very end, after full verification. No intermediate commits.
- **No `.sln` file.** The root `dotnet build` must keep resolving the single plugin csproj so the release pipeline is untouched.

---

## File Ownership (parallel-safe partition)

No two tasks touch the same file.

| Task | Owns |
|---|---|
| 1 Baseline | `JellyfinDiagnostics.csproj`, `Checkers/SecurityChecker.cs`, `Services/UserEnumerator.cs` (new) |
| 2 Models | `Models/ButtonAction.cs`, `Models/HistoryOutcome.cs`, `Models/HistoryEntry.cs` (all new) |
| 3 Service | `Services/HistoryService.cs` (new) |
| 4 Tests | `Tests/JellyfinDiagnostics.Tests/*` (new), `.github/workflows/build.yml` |
| 5 API | `Api/DiagnosticsController.cs`, `Plugin.cs` |
| 6 UI | `Pages/diagnosticsPage.html` |

---

## Locked Contracts

Every task codes against these exact signatures. Do not rename anything here.

```csharp
// Models/ButtonAction.cs
namespace JellyfinDiagnostics.Models;
public enum ButtonAction { RunDiagnostics = 0, ExportReport = 1, AnalyzeWithAi = 2 }

// Models/HistoryOutcome.cs
namespace JellyfinDiagnostics.Models;
public enum HistoryOutcome { Success = 0, Failed = 1 }

// Models/HistoryEntry.cs
namespace JellyfinDiagnostics.Models;
public class HistoryEntry
{
    public string Id { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public ButtonAction Action { get; set; }
    public string UserName { get; set; } = string.Empty;
    public HistoryOutcome Outcome { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public int? CriticalCount { get; set; }
    public int? WarningCount { get; set; }
    public int? InfoCount { get; set; }
    public string? JellyfinVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public bool HasReport { get; set; }
}

// Services/HistoryService.cs
namespace JellyfinDiagnostics.Services;
public class HistoryService
{
    public const int MaxEntries = 500;
    public HistoryService(string rootPath, ILogger<HistoryService> logger);
    public Task<HistoryEntry> RecordAsync(HistoryEntry entry, DiagnosticsReport? snapshot, CancellationToken cancellationToken);
    public Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(int startIndex, int limit, CancellationToken cancellationToken);
    public Task<DiagnosticsReport?> GetReportAsync(string id, CancellationToken cancellationToken);
    public Task ClearAsync(CancellationToken cancellationToken);
}

// Services/UserEnumerator.cs
namespace JellyfinDiagnostics.Services;
public static class UserEnumerator
{
    // Works on 10.11.0-10.11.11. Tries GetUsers() then the Users property. Never throws.
    public static IReadOnlyList<object> GetUsers(object userManager);
}
```

**Storage layout** (root = `Path.Combine(IApplicationPaths.DataPath, "diagnostics-history")`):

```
diagnostics-history/
  index.json          # JSON array of HistoryEntry, oldest first
  reports/<id>.json   # DiagnosticsReport snapshot, one per RunDiagnostics press
```

**JSON:** serialize with default `System.Text.Json` (PascalCase, matching what the page already tolerates via its `f.Title || f.title` fallbacks).

**HTTP contract:**

| Method | Route | Request | Response |
|---|---|---|---|
| GET | `Diagnostics/History?startIndex=0&limit=50` | — | `HistoryEntry[]`, **newest first** |
| GET | `Diagnostics/History/{id}/Report` | — | `DiagnosticsReport`, or 404 |
| POST | `Diagnostics/History/Record` | `{"Action":"ExportReport"}` | `HistoryEntry` (201), or 400 if action not allowlisted |
| DELETE | `Diagnostics/History` | — | 204 |

`POST Record` allowlist: **`ExportReport` only.** `RunDiagnostics` and `AnalyzeWithAi` are recorded server-side and must be rejected with 400 if a client tries to assert them — otherwise the client could forge scan results.

---

## Task 1: Repair the broken baseline

**Files:**
- Modify: `JellyfinDiagnostics.csproj` (package version pins)
- Create: `Services/UserEnumerator.cs`
- Modify: `Checkers/SecurityChecker.cs` (3 call sites: `CountUsers`, `CountAdmins`, `CheckDefaultAdmin`)

**Interfaces:**
- Produces: `UserEnumerator.GetUsers(object userManager)` → `IReadOnlyList<object>`

**Why:** `master` does not compile. `Jellyfin.Controller` floats on `10.11.*`, now resolving 10.11.11, where `IUserManager.Users` was removed in favour of `GetUsers()`. Because the plugin must run on 10.11.0–10.11.11 from one DLL, neither API may be called statically.

- [ ] **Step 1: Pin the package versions**

In `JellyfinDiagnostics.csproj`, replace the floating refs:

```xml
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.11.11" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.11" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test** (`Tests/JellyfinDiagnostics.Tests/UserEnumeratorTests.cs` — create if Task 4 has not yet landed it)

```csharp
using JellyfinDiagnostics.Services;
using Xunit;

namespace JellyfinDiagnostics.Tests;

public class UserEnumeratorTests
{
    // Mimics Jellyfin <= 10.11.6, which exposes a Users property.
    private sealed class OldStyleUserManager
    {
        public IEnumerable<object> Users => new object[] { "alice", "bob" };
    }

    // Mimics Jellyfin >= 10.11.11, which exposes GetUsers().
    private sealed class NewStyleUserManager
    {
        public IEnumerable<object> GetUsers() => new object[] { "carol" };
    }

    private sealed class UnknownUserManager { }

    [Fact]
    public void GetUsers_ReadsUsersProperty_OnOldApi()
    {
        Assert.Equal(2, UserEnumerator.GetUsers(new OldStyleUserManager()).Count);
    }

    [Fact]
    public void GetUsers_CallsGetUsersMethod_OnNewApi()
    {
        Assert.Single(UserEnumerator.GetUsers(new NewStyleUserManager()));
    }

    [Fact]
    public void GetUsers_ReturnsEmpty_WhenNeitherApiExists()
    {
        Assert.Empty(UserEnumerator.GetUsers(new UnknownUserManager()));
    }
}
```

- [ ] **Step 3: Run it and watch it fail**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet test Tests/JellyfinDiagnostics.Tests -v q
```
Expected: FAIL — `UserEnumerator` does not exist.

- [ ] **Step 4: Implement `Services/UserEnumerator.cs`**

```csharp
using System.Collections;

namespace JellyfinDiagnostics.Services;

/// <summary>
/// Enumerates Jellyfin users across incompatible 10.11.x APIs.
/// 10.11.0-10.11.6 expose an IUserManager.Users property; 10.11.11 replaced it
/// with GetUsers(). The plugin ships one DLL for the whole line, so the call
/// must be resolved at runtime rather than bound at compile time.
/// </summary>
public static class UserEnumerator
{
    public static IReadOnlyList<object> GetUsers(object userManager)
    {
        if (userManager == null)
        {
            return Array.Empty<object>();
        }

        var type = userManager.GetType();

        object? raw = null;
        try
        {
            var method = type.GetMethod("GetUsers", Type.EmptyTypes);
            if (method != null)
            {
                raw = method.Invoke(userManager, null);
            }
            else
            {
                raw = type.GetProperty("Users")?.GetValue(userManager);
            }
        }
        catch
        {
            return Array.Empty<object>();
        }

        if (raw is not IEnumerable enumerable)
        {
            return Array.Empty<object>();
        }

        var users = new List<object>();
        foreach (var user in enumerable)
        {
            if (user != null)
            {
                users.Add(user);
            }
        }

        return users;
    }
}
```

- [ ] **Step 5: Rewrite the three `SecurityChecker` call sites**

Replace `_userManager.Users` with `UserEnumerator.GetUsers(_userManager)` in `CountUsers()`, `CountAdmins()`, and `CheckDefaultAdmin()`. The existing reflection-based `IsUserAdmin(object)` helper already accepts a loosely-typed user, so it needs no change. In `CheckDefaultAdmin`, read the username reflectively too, since the element type is now `object`:

```csharp
    private int CountUsers()
    {
        return UserEnumerator.GetUsers(_userManager).Count;
    }

    private int CountAdmins()
    {
        int n = 0;
        foreach (var u in UserEnumerator.GetUsers(_userManager))
        {
            if (IsUserAdmin(u))
            {
                n++;
            }
        }

        return n;
    }

    private void CheckDefaultAdmin(List<DiagnosticResult> results)
    {
        foreach (var user in UserEnumerator.GetUsers(_userManager))
        {
            if (!IsUserAdmin(user))
            {
                continue;
            }

            var name = user.GetType().GetProperty("Username")?.GetValue(user) as string ?? string.Empty;
            var nameLower = name.ToLowerInvariant();
            if (nameLower == "admin" || nameLower == "administrator" || nameLower == "jellyfin" || nameLower == "root")
            {
                // ... existing DiagnosticResult block, unchanged ...
            }
        }
    }
```

Keep the existing try/catch structure of each method. If `IsUserAdmin` is currently typed to a concrete `User`, widen its parameter to `object` — it is already reflection-based inside.

- [ ] **Step 6: Build and test**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet build -c Release
dotnet test Tests/JellyfinDiagnostics.Tests -v q
```
Expected: `Build succeeded. 0 Error(s)` and all `UserEnumeratorTests` green.

- [ ] **Step 7: Audit the other 10 checkers for the same class of bug**

Grep every checker and service for direct member access on Jellyfin interfaces (`IUserManager`, `IServerApplicationHost`, `IServerConfigurationManager`, `IPluginManager`, `ITaskManager`). For each, confirm the member exists in **both** 10.11.0 and 10.11.11 metadata. The compiler only validates against 10.11.11 — it cannot catch a member that is missing on an *older* server. Report anything that differs; convert it to reflection with fallback.

---

## Task 2: History models

**Files:**
- Create: `Models/ButtonAction.cs`, `Models/HistoryOutcome.cs`, `Models/HistoryEntry.cs`

**Interfaces:**
- Produces: the three types exactly as written in **Locked Contracts** above.

- [ ] **Step 1: Create all three files**, copying the code verbatim from the Locked Contracts section. No extra members, no renames.

- [ ] **Step 2: Build**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet build -c Release
```
Expected: `Build succeeded`.

---

## Task 3: HistoryService

**Files:**
- Create: `Services/HistoryService.cs`
- Test: `Tests/JellyfinDiagnostics.Tests/HistoryServiceTests.cs`

**Interfaces:**
- Consumes: `HistoryEntry`, `ButtonAction`, `HistoryOutcome` (Task 2); `DiagnosticsReport` (existing, `Models/DiagnosticsReport.cs`)
- Produces: `HistoryService` exactly as in **Locked Contracts**.

**Design notes the implementer must honour:**
- Constructor takes a **plain `string rootPath`** — never `IApplicationPaths`. This is what makes it unit-testable against a temp dir.
- The constructor must not throw if the directory does not exist; create it lazily.
- One `SemaphoreSlim(1,1)` guards every read-modify-write of `index.json`.
- Writes are **atomic**: serialize to `index.json.tmp`, then `File.Move(tmp, index.json, overwrite: true)`. A process killed mid-write must never leave a truncated index.
- `index.json` is stored **oldest-first**; `GetHistoryAsync` returns **newest-first**. Get this backwards and the UI shows the wrong rows.
- Trim to `MaxEntries` (500) on every record: drop the oldest rows and `File.Delete` each dropped entry's snapshot file.
- A snapshot is written **only** when `snapshot != null` (i.e. RunDiagnostics). Set `HasReport` accordingly.
- `RecordAsync` assigns `Id` (a new GUID string) if the caller left it empty, and stamps `Timestamp = DateTime.UtcNow` if unset.
- If `index.json` fails to deserialize, move it to `index.json.corrupt` (overwriting any previous one), log a warning, and continue with an empty index. Never throw out of a read.
- `GetReportAsync` returns `null` for an unknown id or a missing/corrupt snapshot file — never throws.
- Guard the snapshot path against traversal: reject any `id` that is not a parseable GUID before touching the filesystem.

- [ ] **Step 1: Write the failing tests**

```csharp
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
}
```

- [ ] **Step 2: Run them and watch them fail**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet test Tests/JellyfinDiagnostics.Tests -v q
```
Expected: FAIL — `HistoryService` does not exist.

- [ ] **Step 3: Implement `Services/HistoryService.cs`** to satisfy every design note above and make all 12 tests pass.

- [ ] **Step 4: Run them and watch them pass**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet test Tests/JellyfinDiagnostics.Tests -v q
```
Expected: all green.

---

## Task 4: Test project + CI

**Files:**
- Create: `Tests/JellyfinDiagnostics.Tests/JellyfinDiagnostics.Tests.csproj`
- Modify: `.github/workflows/build.yml`
- Modify: `.gitignore` (add `bin/`, `obj/` if not already covered)

**Interfaces:**
- Produces: a `dotnet test Tests/JellyfinDiagnostics.Tests` target that Tasks 1 and 3 rely on.

**Critical:** do **not** add a `.sln`. The release workflow runs a bare `dotnet build` at the repo root, which only works while exactly one csproj sits there. A solution file would change what that command builds.

- [ ] **Step 1: Create the test csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../JellyfinDiagnostics.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Verify the root build is unaffected**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet build -c Release
ls bin/Release/net9.0/JellyfinDiagnostics.dll
```
Expected: build succeeds and the DLL is at the exact path the release job copies from.

- [ ] **Step 3: Add a test step to CI**

In `.github/workflows/build.yml`, in the `build` job, after the `Build` step and before `Upload artifact`:

```yaml
      - name: Test
        run: dotnet test Tests/JellyfinDiagnostics.Tests -c Release --verbosity normal
```

Leave the `release` job alone.

---

## Task 5: API — record presses and serve history

**Files:**
- Modify: `Api/DiagnosticsController.cs`
- Modify: `Plugin.cs` (DI registration)

**Interfaces:**
- Consumes: `HistoryService`, `HistoryEntry`, `ButtonAction`, `HistoryOutcome`
- Produces: the four HTTP endpoints in the **HTTP contract** table.

- [ ] **Step 1: Register `HistoryService` in `Plugin.cs`**

`HistoryService` takes a `string`, so it needs a factory rather than plain `AddSingleton<T>`:

```csharp
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

        serviceCollection.AddSingleton<HistoryService>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<HistoryService>>();
            var root = Path.Combine(paths.DataPath, "diagnostics-history");
            return new HistoryService(root, logger);
        });
```

(`IApplicationPaths.DataPath` is present and identical in 10.11.0 through 10.11.11 — verified against package metadata.)

- [ ] **Step 2: Inject `HistoryService` into the controller** and add a username helper.

Jellyfin's auth populates the `ClaimsPrincipal`. Read it defensively — never let a missing claim break a scan:

```csharp
    private string CurrentUserName()
    {
        var name = User?.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return User?.FindFirst("Jellyfin-UserId")?.Value ?? "unknown";
    }
```

- [ ] **Step 3: Record the Run press inside `RunDiagnostics`**

Wrap the existing call. Record success *and* failure, with duration and summary counts. A history write failure must be swallowed:

```csharp
    [HttpGet("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDiagnostics(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var report = await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            await RecordSafelyAsync(new HistoryEntry
            {
                Action = ButtonAction.RunDiagnostics,
                UserName = CurrentUserName(),
                Outcome = HistoryOutcome.Success,
                DurationMs = stopwatch.ElapsedMilliseconds,
                CriticalCount = report.Results.Count(r => r.Severity == DiagnosticSeverity.Critical),
                WarningCount = report.Results.Count(r => r.Severity == DiagnosticSeverity.Warning),
                InfoCount = report.Results.Count(r => r.Severity == DiagnosticSeverity.Info),
                JellyfinVersion = report.JellyfinVersion,
                OperatingSystem = report.OperatingSystem
            }, report, cancellationToken).ConfigureAwait(false);

            return Ok(report);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await RecordSafelyAsync(new HistoryEntry
            {
                Action = ButtonAction.RunDiagnostics,
                UserName = CurrentUserName(),
                Outcome = HistoryOutcome.Failed,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            }, null, cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    // History is observability, not core function. Never fail the user's scan
    // because we could not write a log line.
    private async Task RecordSafelyAsync(HistoryEntry entry, DiagnosticsReport? snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await _historyService.RecordAsync(entry, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record button press history");
        }
    }
```

Confirm the exact `DiagnosticSeverity` member names against `Models/DiagnosticSeverity.cs` before writing the counts (the page maps 0=Info, 1=Warning, 2=Critical). Inject `ILogger<DiagnosticsController>` for `RecordSafelyAsync`.

- [ ] **Step 4: Record the AI press inside `SendToAi`**

Same shape: stopwatch, `ButtonAction.AnalyzeWithAi`, `Success` on a returned response, `Failed` + `ex.Message` on throw. Pass `null` for the snapshot — the report already belongs to the Run entry. The existing early `BadRequest` guards (AI disabled, no endpoint URL) are **not** button-press failures worth recording; return them unchanged before starting the stopwatch.

- [ ] **Step 5: Add the four history endpoints**

```csharp
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var entries = await _historyService.GetHistoryAsync(startIndex, limit, cancellationToken).ConfigureAwait(false);
        return Ok(entries);
    }

    [HttpGet("History/{id}/Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistoryReport([FromRoute] string id, CancellationToken cancellationToken)
    {
        var report = await _historyService.GetReportAsync(id, cancellationToken).ConfigureAwait(false);
        return report == null ? NotFound() : Ok(report);
    }

    [HttpPost("History/Record")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecordPress([FromBody] RecordPressRequest request, CancellationToken cancellationToken)
    {
        // Only client-side-only buttons may be self-reported. RunDiagnostics and
        // AnalyzeWithAi are recorded server-side from their real outcome; letting a
        // client assert them would allow forged scan results.
        if (request?.Action != ButtonAction.ExportReport)
        {
            return BadRequest(new { error = "Only ExportReport may be recorded from the client." });
        }

        var saved = await _historyService.RecordAsync(new HistoryEntry
        {
            Action = ButtonAction.ExportReport,
            UserName = CurrentUserName(),
            Outcome = HistoryOutcome.Success
        }, null, cancellationToken).ConfigureAwait(false);

        return Ok(saved);
    }

    [HttpDelete("History")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearHistory(CancellationToken cancellationToken)
    {
        await _historyService.ClearAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
```

Add the request DTO in the same file:

```csharp
public class RecordPressRequest
{
    public ButtonAction Action { get; set; }
}
```

The page posts `{"Action":"ExportReport"}` as a string, so configure the enum to bind from its name — add `[JsonConverter(typeof(JsonStringEnumConverter))]` to the `Action` property, or post the numeric value from the page. **Pick one and make the page match.**

- [ ] **Step 6: Build**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
dotnet build -c Release
```
Expected: `Build succeeded. 0 Error(s)`.

---

## Task 6: History UI

**Files:**
- Modify: `Pages/diagnosticsPage.html`

**Interfaces:**
- Consumes: the four endpoints in the **HTTP contract** table.

**Constraints:** `textContent` only — no `innerHTML`. Reuse the existing `el()` / `clearNode()` helpers and the `diag-category-section` look. Keep every string ASCII-safe with the same `\uXXXX` escape style the file already uses.

- [ ] **Step 1: Add the History section markup** after `.diag-content`:

```html
                <div class="diag-history-section">
                    <div class="diag-category-section">
                        <div class="diag-category-header diag-history-header">
                            <h2>History</h2>
                            <span class="diag-category-toggle">▼</span>
                        </div>
                        <div class="diag-category-body diag-history-body">
                            <div class="diag-history-content"></div>
                        </div>
                    </div>
                </div>
```

- [ ] **Step 2: Add table styles** alongside the existing `<style>` rules (`.diag-history-table`, `th`, `td`, `.diag-history-empty`, `.diag-history-error`). Match the existing dark palette (`#1a1a1a` surfaces, `#333` borders, `#00a4dc` accent).

- [ ] **Step 3: Render the history table**

```javascript
                function actionLabel(action) {
                    if (action === 0 || action === 'RunDiagnostics') return 'Run Diagnostics';
                    if (action === 1 || action === 'ExportReport') return 'Export Report';
                    if (action === 2 || action === 'AnalyzeWithAi') return 'Analyze with AI';
                    return 'Unknown';
                }

                function loadHistory() {
                    var host = page.querySelector('.diag-history-content');
                    ApiClient.ajax({
                        type: 'GET',
                        url: ApiClient.getUrl('Diagnostics/History', { startIndex: 0, limit: 50 }),
                        dataType: 'json'
                    }).then(function (entries) {
                        renderHistory(entries || []);
                    }).catch(function () {
                        clearNode(host);
                        host.appendChild(el('div', 'diag-history-error', 'Could not load history.'));
                    });
                }
```

`renderHistory(entries)` builds a `<table>` with headers Time / Action / User / Result. For each entry: `new Date(e.Timestamp || e.timestamp).toLocaleString()`, `actionLabel(...)`, the username, and a result cell — for a successful Run show `❌ n  ⚠️ n  ✅ n` from the counts; for a failure show the error message in `#f44336`; otherwise `✅`. Rows whose entry `HasReport` is true get a **View** button appended to the row.

Handle both PascalCase and camelCase keys, exactly as the existing `getSev()` / `f.Title || f.title` code does.

- [ ] **Step 4: Wire View to re-render a past report**

```javascript
                function viewHistoryReport(id) {
                    ApiClient.ajax({
                        type: 'GET',
                        url: ApiClient.getUrl('Diagnostics/History/' + id + '/Report'),
                        dataType: 'json'
                    }).then(function (report) {
                        currentReport = report;
                        renderReport(report);
                        page.querySelector('.diag-export-btn').disabled = false;
                        window.scrollTo(0, 0);
                    });
                }
```

This deliberately reuses the existing `renderReport()` — do not duplicate rendering logic.

- [ ] **Step 5: Record the Export press**

In `exportReport()`, after the download is triggered, POST the press. Fire-and-forget; a history failure must not break the download:

```javascript
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('Diagnostics/History/Record'),
                        data: JSON.stringify({ Action: 'ExportReport' }),
                        contentType: 'application/json',
                        dataType: 'json'
                    }).then(loadHistory).catch(function () { /* history is best-effort */ });
```

Match the serialization choice made in Task 5 Step 5 — string name vs numeric value.

- [ ] **Step 6: Add a Clear history button** inside the history section, behind `confirm()`, calling `DELETE Diagnostics/History` then `loadHistory()`.

- [ ] **Step 7: Refresh history after every press.** Call `loadHistory()` on `pageshow`, and after `runDiagnostics()` completes (success *and* failure), after `exportReport()`, and after `sendToAi()` resolves.

- [ ] **Step 8: Wire the header toggle** to collapse `.diag-history-body`, matching the existing category toggle behaviour.

---

## Task 7: Verify and commit

- [ ] **Step 1: Full local verification**

```bash
export PATH="/home/kasm-user/dotnet9:$PATH"
cd /home/kasm-user/jellyfin-plugin-diagnostics
dotnet build -c Release
dotnet test Tests/JellyfinDiagnostics.Tests -c Release
ls -l bin/Release/net9.0/JellyfinDiagnostics.dll
```
Expected: build succeeds with 0 errors, **every** test passes, and the DLL exists at the path the release job copies.

- [ ] **Step 2: Confirm no `.sln` was added and the release path is intact**

```bash
find . -name "*.sln" -not -path "./.git/*"   # must print nothing
```

- [ ] **Step 3: Bump the version and changelog.** `JellyfinDiagnostics.csproj` `<Version>` to `1.1.0.0`; add a `## [1.1.0.0]` section to `CHANGELOG.md` covering both the history feature and the 10.11.11 compatibility fix.

- [ ] **Step 4: ONE commit** covering the baseline repair and the feature together, per the owner's decision.

```bash
git add -A
git commit -m "feat: persist button press history to disk with dashboard view

Records every Run Diagnostics / Export Report / Analyze with AI press to
diagnostics-history/ (index + per-run report snapshots, 500-entry cap) and
surfaces them in a new History section on the dashboard.

Also repairs the build, which broke when the floating Jellyfin.Controller
10.11.* reference began resolving 10.11.11, where IUserManager.Users was
replaced by GetUsers(). Packages are now pinned, and user enumeration goes
through reflection so one DLL still runs on 10.11.0-10.11.11."
```

---

## Self-Review

**Spec coverage:** storage layout → Task 3. Data model → Task 2. All three buttons → Task 5 (Run, AI) + Tasks 5/6 (Export). 500-trim → Task 3. Atomic write + corruption recovery → Task 3. API → Task 5. UI + View + Clear → Task 6. Tests → Tasks 3/4. Baseline repair → Task 1. Cross-version audit → Task 1 Step 7. CI test step → Task 4.

**Type consistency:** `HistoryService.MaxEntries`, `RecordAsync(entry, snapshot, ct)`, `GetHistoryAsync(startIndex, limit, ct)`, `GetReportAsync(id, ct)`, `ClearAsync(ct)` and `UserEnumerator.GetUsers(object)` are used identically in every task that references them. `HistoryEntry` field names match between the C# model, the HTTP contract, and the UI's PascalCase/camelCase handling.

**Known coupling to resolve during execution:** the `ButtonAction` JSON encoding (string name vs number) is chosen in Task 5 Step 5 and must be mirrored in Task 6 Step 5. This is the one place two parallel tasks must agree at runtime; the verifier must exercise an Export press end-to-end.
