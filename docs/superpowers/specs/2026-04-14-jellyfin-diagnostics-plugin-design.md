# Jellyfin AI Diagnostics Plugin — Design Spec

## Overview

A server-side Jellyfin admin plugin targeting Jellyfin 10.11.6 on Docker/Unraid. Analyzes server configuration and environment to detect common issues, presents findings in the Admin UI, and optionally integrates with an external AI API for analysis.

**Key constraints:**
- Read-only by default — never auto-fixes anything
- Safe-by-default — no network calls unless admin explicitly triggers
- No host-level access beyond what's mounted into the Docker container
- Targets .NET 8.0 and Jellyfin Plugin API v10.11.x

## Architecture

### Pattern: Modular Service Architecture

Each detection area is an independent `IDiagnosticChecker` implementation. A central `DiagnosticsService` orchestrates all checkers. A REST API controller exposes results. An embedded HTML page provides the admin dashboard.

### Data Flow

```
Admin clicks "Run Diagnostics"
  → JS calls GET /Diagnostics/Run
  → DiagnosticsController calls DiagnosticsService.RunAllAsync()
  → Service iterates all IDiagnosticChecker implementations
  → Each checker returns List<DiagnosticResult>
  → Service aggregates into DiagnosticsReport (timestamp, server info, results)
  → JSON returned to frontend
  → Rendered as categorized cards with severity badges
```

## File Structure

```
jellyfin-plugin-diagnostics/
├── JellyfinDiagnostics.csproj          # net8.0, refs Jellyfin.Controller + Jellyfin.Model
├── Plugin.cs                           # IPlugin entry point, registers services
├── PluginConfiguration.cs              # config model (AI toggle, endpoint URL, API key)
├── Models/
│   ├── DiagnosticSeverity.cs           # enum: Info, Warning, Critical
│   ├── DiagnosticResult.cs             # single finding
│   └── DiagnosticsReport.cs            # collection of results + metadata
├── Checkers/
│   ├── IDiagnosticChecker.cs           # interface: Name, RunAsync()
│   ├── HardwareAccelerationChecker.cs  # GPU, device nodes, FFmpeg, log patterns
│   ├── VolumePathChecker.cs            # library paths, mounts, UID/GID
│   ├── PermissionsChecker.cs           # writability, SQLite errors
│   └── NetworkChecker.cs              # HTTP/HTTPS, published URL, bitrate caps
├── Services/
│   ├── DiagnosticsService.cs           # orchestrates all checkers
│   ├── LogAnalyzer.cs                  # parses Jellyfin logs for error patterns
│   └── AiIntegrationService.cs         # optional sanitized AI integration
├── Api/
│   └── DiagnosticsController.cs        # REST endpoints
└── Pages/
    └── diagnosticsPage.html            # embedded admin dashboard
```

## Models

### DiagnosticSeverity (enum)
- `Info` — informational, no action needed
- `Warning` — potential issue, should investigate
- `Critical` — broken or misconfigured, needs attention

### DiagnosticStatus (enum)
- `Working` — functioning correctly
- `Degraded` — configured but partially unavailable
- `Broken` — misconfigured or non-functional
- `Unknown` — could not determine

### DiagnosticResult
| Field | Type | Description |
|-------|------|-------------|
| Severity | DiagnosticSeverity | Issue severity level |
| Status | DiagnosticStatus | Current operational status |
| Category | string | Detection area (e.g., "Hardware Acceleration") |
| Title | string | Short description (e.g., "VAAPI device not found") |
| Detail | string | What is wrong |
| UnraidContext | string | Why this happens on Unraid/Docker specifically |
| FixSteps | List\<string\> | Step-by-step human-readable fix instructions |

### DiagnosticsReport
| Field | Type | Description |
|-------|------|-------------|
| Timestamp | DateTime | When the scan was run |
| JellyfinVersion | string | Server version |
| OperatingSystem | string | OS info from inside container |
| Results | List\<DiagnosticResult\> | All findings |

## Checkers

### IDiagnosticChecker Interface
```csharp
public interface IDiagnosticChecker
{
    string Name { get; }
    string Category { get; }
    Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken);
}
```

### 1. HardwareAccelerationChecker
**Detects:**
- HW acceleration enabled/disabled in ServerConfiguration
- Selected acceleration type (NVENC, VAAPI, QSV, none)
- NVIDIA device nodes: `/dev/nvidia*` existence, `nvidia-smi` availability and output
- VAAPI/QSV device nodes: `/dev/dri` existence and contents (renderD128, card0)
- FFmpeg encoder availability: runs `ffmpeg -encoders` and checks for h264_nvenc, h264_vaapi, h264_qsv
- Log patterns: scans for transcode failure messages, codec errors, device access denied

**Status output:**
- Working: HW accel enabled + device present + encoders available + no log errors
- Degraded: HW accel enabled + some issues (e.g., device present but log errors)
- Broken: HW accel enabled but device missing or encoders unavailable

### 2. VolumePathChecker
**Detects:**
- Enumerates all Jellyfin library virtual folders and their filesystem paths
- Verifies each path exists inside the container
- Checks for common Unraid mapping errors:
  - `/mnt/user` paths that are missing (not mounted)
  - Read-only mounts (parses `/proc/mounts`)
  - UID/GID mismatch: compares running process UID/GID against file ownership (Unraid typical: 99:100)
- Flags libraries pointing to non-existent or inaccessible paths

### 3. PermissionsChecker
**Detects:**
- Config directory writability (creates + deletes temp file)
- Cache directory writability
- Metadata directory writability
- Permission-denied errors in Jellyfin logs
- SQLite write failure patterns in logs (SQLITE_READONLY, database is locked)

**Safety:** The only "write" operation is creating a zero-byte temp file to test writability. The file is immediately deleted in a finally block.

### 4. NetworkChecker
**Detects:**
- HTTP vs HTTPS configuration mismatch (e.g., HTTPS enabled but no valid cert configured)
- Published server URL issues (empty, points to localhost, uses wrong port)
- Encoding options: remote bitrate cap set too low → causes excessive transcoding
- Base URL configuration issues

## Services

### DiagnosticsService
- Receives all `IDiagnosticChecker` implementations via DI
- `RunAllAsync()`: runs all checkers, catches per-checker exceptions (logs and continues), aggregates results into `DiagnosticsReport`
- Populates report metadata (timestamp, Jellyfin version, OS info)

### LogAnalyzer
- Shared utility used by multiple checkers
- Reads Jellyfin log files from the configured log directory
- Scans last N lines (configurable, default 5000) for regex patterns
- Returns matching log entries grouped by pattern category
- Patterns include: transcode errors, permission denied, SQLite failures, network binding errors

### AiIntegrationService
- **OFF by default** — requires admin to enable in plugin config
- `SanitizeReport(DiagnosticsReport)`: strips PII before sending:
  - File paths anonymized: `/mnt/user/media/movies` → `[media_path_1]`
  - IP addresses, hostnames, MAC addresses removed
  - User names, tokens, API keys removed
  - Only diagnostic findings, severity, and generic system info retained
- `SendToAiAsync(DiagnosticsReport)`: POSTs sanitized JSON to configured endpoint
- Never called automatically — only on explicit admin button click

## API Endpoints

All endpoints require admin authentication (Jellyfin's built-in auth).

| Method | Path | Description |
|--------|------|-------------|
| GET | /Diagnostics/Run | Run all checkers and return report JSON |
| GET | /Diagnostics/Report | Get last run report (or run if none cached) |
| POST | /Diagnostics/Ai | Send sanitized report to configured AI endpoint |

## Admin Dashboard Page

### Registration
- Registered via `IPluginConfigurationPage` as an admin menu item
- Single self-contained HTML file with inline CSS and JS
- No external dependencies or build tools

### Layout
- **Header:** Plugin name + version, "Run Diagnostics" button
- **Summary bar:** Count badges for Critical / Warning / Info findings
- **Category sections:** Collapsible cards for each detection area
- **Each finding:** Severity icon (green check / yellow warning / red X), title, detail, Unraid context explanation, numbered fix steps
- **Export button:** Downloads report as JSON file
- **AI button:** Only visible when AI integration enabled in config; sends sanitized report, shows response in modal

### Styling
- Follows Jellyfin's admin dark theme using CSS variables
- Responsive layout for various screen sizes

## Plugin Configuration (PluginConfiguration)

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| EnableAiIntegration | bool | false | Toggle AI integration |
| AiEndpointUrl | string | "" | External AI API URL |
| AiApiKey | string | "" | API key for AI endpoint |
| LogLinesToScan | int | 5000 | Number of recent log lines to analyze |

## Safety & Security

- **Read-only:** No Jellyfin configuration is ever modified
- **No auto-fix:** All fixes are presented as instructions for the admin
- **No background tasks:** Scans only run when admin triggers them
- **No network calls:** Unless admin explicitly clicks "Send to AI" button
- **PII stripping:** AI integration sanitizes all identifying info before sending
- **Admin-only:** All API endpoints require admin authentication
- **Exception isolation:** Each checker is wrapped in try/catch — one failing checker doesn't break the whole scan

## Build & Install Notes

- Target: `net8.0`
- NuGet references: `Jellyfin.Controller`, `Jellyfin.Model` (version 10.11.x)
- Build produces a single DLL
- Install: copy DLL to Jellyfin's plugin directory (`/config/plugins/JellyfinDiagnostics/`)
- Create `meta.json` with plugin GUID, name, version for Jellyfin to discover it

## What This Plugin Cannot Do

- Cannot access the Unraid host filesystem directly (only what's mounted into the Docker container)
- Cannot modify Docker container settings (e.g., add device passthrough)
- Cannot restart services or the container
- Cannot install system packages (e.g., nvidia-smi if not already in the container)
- Device node detection (`/dev/dri`, `/dev/nvidia*`) works by checking existence, not by testing actual GPU functionality
