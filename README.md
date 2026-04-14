# Jellyfin AI Diagnostics Plugin

A server-side Jellyfin admin plugin that detects common Docker/Unraid configuration issues in Jellyfin 10.11.6 and presents findings in an admin dashboard.

**Targets:** Jellyfin 10.11.6 · .NET 8.0 · Docker on Unraid

## What it checks

- **Hardware acceleration:** GPU device nodes (`/dev/dri`, `/dev/nvidia*`), FFmpeg encoder availability, NVIDIA runtime status, transcode errors in logs
- **Volumes & paths:** Library path existence, read-only mounts, UID/GID mismatch (Unraid default 99:100)
- **Permissions:** Writability of config, cache, data, metadata, log directories; SQLite and permission errors in logs
- **Networking:** HTTPS certificate validity, published server URIs, base URL, remote streaming bitrate caps

## Safety

- **Read-only by default** — no auto-fix, no config modification
- **No network calls** unless admin explicitly clicks "Analyze with AI"
- **PII stripping** — the AI integration sanitizes all file paths, IPs, MACs, usernames before sending
- **Admin-only** — all endpoints require Jellyfin's `RequiresElevation` policy

## Building

```bash
dotnet build -c Release
```

Output: `bin/Release/net8.0/JellyfinDiagnostics.dll`

## Installing

1. Copy `JellyfinDiagnostics.dll` and `meta.json` to `/config/plugins/JellyfinDiagnostics/` inside the Jellyfin container
2. Restart the Jellyfin container
3. Navigate to Dashboard → Plugins → Jellyfin Diagnostics → Settings

## Manual Testing Checklist

The following MUST be tested manually against a real Jellyfin 10.11.6 instance because there is no integration test harness:

### Plugin loading
- [ ] Plugin appears in Dashboard → Plugins list after DLL copy + restart
- [ ] "Diagnostics" entry appears in the admin menu (from `EnableInMainMenu = true`)
- [ ] Clicking "Diagnostics" loads the embedded HTML page without 404

### API endpoints
- [ ] `GET /Diagnostics/Run` returns 200 with JSON report when called with admin auth
- [ ] Same endpoint returns 401 without admin auth (verify `RequiresElevation` policy works)
- [ ] `GET /Diagnostics/Report/Download` triggers a JSON file download

### Hardware acceleration detection
- [ ] With HW accel set to `none`, reports "Hardware acceleration is disabled" (Info)
- [ ] With NVENC selected but no NVIDIA runtime, reports "NVIDIA device nodes not found" (Critical)
- [ ] With VAAPI selected and `/dev/dri/renderD128` mapped, reports "renderD128 present" (Info)
- [ ] FFmpeg encoder check runs against the correct FFmpeg path (bundled jellyfin-ffmpeg vs custom)

### Volume & path detection
- [ ] Non-existent library path reports "Library path does not exist" (Critical)
- [ ] Read-only mount reports "Read-only mount detected" (Warning) — test by mapping a volume as `:ro`
- [ ] When running as UID 99 / GID 100, reports "UID/GID matches Unraid defaults" (Info)
- [ ] When running as 1000:1000, reports "Non-standard UID/GID" (Warning)

### Permissions detection
- [ ] With writable config dir, reports "Config directory is writable" (Info)
- [ ] With read-only config dir (test by chmod), reports "Config directory is NOT writable" (Critical)
- [ ] Temp test file is cleaned up after writability check (verify no `.diag_write_test_*` files remain)

### Network detection
- [ ] HTTPS enabled without cert reports Critical
- [ ] No published URIs reports Info with Unraid context suggestion
- [ ] Published URI containing "localhost" or "127.0.0.1" reports Critical
- [ ] Low bitrate cap (<8 Mbps) reports Warning

### AI integration
- [ ] With AI disabled in config, "Analyze with AI" button is hidden
- [ ] With AI enabled but no endpoint URL, POST /Diagnostics/Ai returns 400
- [ ] Sanitized report contains `[IP_REDACTED]`, `[MAC_REDACTED]`, `[path_N]` instead of real values
- [ ] Sanitized report does NOT contain the API key or raw file paths

### UI / XSS safety
- [ ] All dynamic content uses `textContent`, not `innerHTML`
- [ ] Injecting a library path like `<script>alert(1)</script>` (rename a library to this) renders as escaped text, NOT executed script

## Known limitations

- **Reflection-free network config:** Uses strongly-typed `NetworkConfiguration` — if Jellyfin removes/renames these properties in a future 10.11.x point release, the checker will need updating.
- **PublishedServerUriBySubnet format:** Jellyfin uses `subnet=url` format; the plugin only validates URLs, not subnet CIDR correctness.
- **nvidia-smi process invocation:** Depends on the binary being in PATH inside the container. Some custom images may lack it even when NVIDIA runtime works.
- **No access to Unraid host:** Cannot read `/boot/config/` or Unraid syslog directly — all checks are from inside the container only.
- **FFmpeg encoder check runs a subprocess:** Can add 1-3 seconds to the scan time.

## License

MIT
