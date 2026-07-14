# Changelog

## [1.1.0.0] - 2026-07-13

### Added
- Button press history: every Run Diagnostics / Export Report / Analyze with AI press is
  persisted to disk and shown in a new History section on the Diagnostics dashboard, with
  timestamp, user, outcome, duration and finding counts.
- Each Run Diagnostics press stores a full report snapshot, viewable later from the History
  table ("View" reopens the findings from that run).
- History endpoints (all admin-only, under the existing `RequiresElevation` policy):
  `GET Diagnostics/History`, `GET Diagnostics/History/{id}/Report`,
  `POST Diagnostics/History/Record`, `DELETE Diagnostics/History`.
- History is capped at 500 entries; older rows and their snapshots are pruned automatically.

### Fixed
- **Jellyfin 10.11.11 compatibility.** `IUserManager.Users` was removed in 10.11.11 in favour
  of `GetUsers()`, which does not exist on 10.11.6 and earlier. The plugin previously bound to
  one of them at compile time, so a single DLL could not run across the 10.11.x line. User
  enumeration now resolves the API at runtime (`Services/UserEnumerator.cs`), and the shipped
  DLL works on **Jellyfin 10.11.0 through 10.11.11**. `Jellyfin.Controller` / `Jellyfin.Model`
  are pinned to exactly 10.11.11 rather than floating on `10.11.*`.
- Admin detection in the security checker now reads the real user entity's `Permissions`
  collection reflectively, instead of a `Policy` property that no longer exists.
- **Findings are no longer all rendered as "healthy".** Jellyfin serializes enums by name
  (`{"Severity":"Critical"}`), but the dashboard compared severity against `0/1/2`, so every
  comparison was false: the summary bar always read 0 Critical / 0 Warning / 0 Info and every
  finding and category showed the green check. Severity is now normalized from both the string
  and numeric forms.
- **A history index that cannot be read is no longer destroyed.** A transient read failure
  (a backup/antivirus process holding `index.json`, an NFS/SMB hiccup, an EMFILE burst) was
  reported as "no history", and the next press atomically overwrote all 500 rows with one. An
  unreadable index now aborts the record instead, and a zero-length `index.json` — the residue
  of a torn write — is quarantined rather than treated as empty.
- **The sqlite3 integrity check now honours its 10-second cap.** It read stdout to EOF before
  waiting, which meant `WaitForExit(10000)` was only reached once the child had already exited;
  `PRAGMA quick_check` on a multi-GB `library.db` on slow storage hung `GET /Diagnostics/Run`
  for minutes. Both pipes are now drained concurrently under a real timeout
  (`Services/ProcessRunner.cs`), which also fixes a permanent deadlock if the child filled the
  stderr pipe buffer.
- Clear History now also removes `index.json.corrupt` and `index.json.tmp`, which were keeping
  usernames and error strings on disk after the admin purged the history.
- A single unloadable report from the History table no longer wipes the whole rendered table.
- "Analyze with AI" is hidden while a saved report from History is on screen: the AI endpoint
  analyses the server's current report, so it would have sent something other than what the
  admin was reading.

### Safety
- `POST Diagnostics/History/Record` accepts **only** `ExportReport`. Run and AI outcomes are
  recorded server-side from what actually happened, so a client cannot forge scan results into
  the history.
- History report ids must parse as GUIDs before touching the filesystem (path-traversal guard).
- A history write failure can never fail a user's diagnostics run — it is logged and ignored.
- The new History UI renders with `textContent` only; no `innerHTML`.

## [1.0.0.0] - 2026-04-14

### Added
- Hardware acceleration checker (NVENC, VAAPI, QSV, AMF, VideoToolbox, V4L2M2M, RKMPP)
- Volume & path checker (library paths, mount points, UID/GID)
- Permissions checker (config/cache/data/log writability, SQLite errors)
- Network checker (HTTPS, PublishedServerUriBySubnet, base URL, bitrate caps)
- Log analyzer service with regex pattern scanning
- Optional AI integration via OpenAI-compatible endpoint
- PII sanitization (IP, MAC, path, username redaction)
- Admin dashboard HTML page with categorized findings
- REST API with RequiresElevation admin auth
- GitHub Actions build + release workflow
- Jellyfin plugin repository manifest for one-click install

### Safety
- Read-only by default, no auto-fix
- No background network calls
- AI integration disabled by default
- All endpoints require admin authentication
- XSS-safe UI (textContent only, no innerHTML)
