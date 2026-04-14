# Jellyfin AI Diagnostics Plugin

A server-side Jellyfin admin plugin that detects common Docker/Unraid configuration issues in Jellyfin 10.11.6 and presents findings in an admin dashboard. Optionally forwards a **sanitized** report to any OpenAI-compatible AI endpoint for human-readable analysis.

**Targets:** Jellyfin 10.11.6 · .NET 8.0 · Docker on Unraid

---

## What it checks

| Area | Checks |
|---|---|
| **Hardware acceleration** | HW accel type (nvenc/vaapi/qsv/amf/videotoolbox/v4l2m2m/rkmpp), `/dev/dri/renderD128`, `/dev/nvidiactl`, `/dev/nvidia0`, `/dev/nvidia-uvm`, `nvidia-smi` availability, FFmpeg encoder presence, transcode errors in logs |
| **Volumes & paths** | Library path existence & readability, read-only mounts in `/proc/mounts`, UID/GID vs Unraid default 99:100 |
| **Permissions** | Writability of config / cache / data / metadata / log dirs (safe temp-file test), SQLite errors and permission-denied patterns in logs |
| **Networking** | HTTPS cert validity, `PublishedServerUriBySubnet` sanity, base URL format, remote streaming bitrate caps |

Each finding includes severity (Info / Warning / Critical), status (Working / Degraded / Broken), an Unraid-specific context explanation, and numbered fix steps.

## Safety guarantees

- **Read-only by default** — the plugin never modifies Jellyfin config
- **No auto-fix** — every finding is presented as instructions for the admin
- **No background network calls** — scans run only when an admin clicks "Run Diagnostics"
- **No AI traffic unless explicitly enabled** — AI integration is OFF by default and requires opt-in configuration
- **PII stripping** — when AI is enabled, all file paths, IP addresses, MAC addresses, and usernames are redacted before transmission
- **Admin-only** — every API endpoint requires Jellyfin's `RequiresElevation` authorization policy
- **XSS-safe UI** — the dashboard uses `document.createElement` + `textContent` for all dynamic content, never `innerHTML`

---

## Installing

### Option A — Pre-built DLL (recommended for end users)

1. Grab the latest `JellyfinDiagnostics.dll` and `meta.json` from the [Releases page](https://github.com/Atvriders/jellyfin-plugin-diagnostics/releases) (or build them yourself — see Option B).
2. On your Unraid host, create the plugin directory **inside the Jellyfin appdata path**:
   ```bash
   mkdir -p /mnt/user/appdata/jellyfin/plugins/JellyfinDiagnostics_1.0.0.0
   ```
   The exact path depends on your container's `/config` volume mapping. For the official `jellyfin/jellyfin` image, `/config` usually maps to `/mnt/user/appdata/jellyfin`. For `linuxserver/jellyfin`, it maps to the same location by default.
3. Copy both files into that directory:
   ```bash
   cp JellyfinDiagnostics.dll meta.json \
     /mnt/user/appdata/jellyfin/plugins/JellyfinDiagnostics_1.0.0.0/
   ```
4. Fix ownership so Jellyfin can read the files (Unraid default UID/GID 99:100):
   ```bash
   chown -R 99:100 /mnt/user/appdata/jellyfin/plugins/JellyfinDiagnostics_1.0.0.0
   ```
5. **Restart the Jellyfin container** from the Unraid Docker tab (or `docker restart jellyfin`).
6. In the Jellyfin web UI, navigate to **Dashboard → Plugins**. You should see "Jellyfin Diagnostics" in the list with status *Active*.
7. A new **"Diagnostics"** entry appears in the admin side menu. Click it to open the dashboard.

### Option B — Build from source

You need the **.NET 8.0 SDK** installed (on the build machine, not on Unraid):

```bash
git clone https://github.com/Atvriders/jellyfin-plugin-diagnostics.git
cd jellyfin-plugin-diagnostics
dotnet restore
dotnet build -c Release
```

The output DLL will be at `bin/Release/net8.0/JellyfinDiagnostics.dll`. Then follow steps 2–7 of Option A.

### Option C — Jellyfin plugin repository (optional, self-hosted)

You can host `meta.json` as a Jellyfin plugin repository so the plugin installs/updates through the Jellyfin UI instead of manual file copy. Host the DLL behind an HTTPS URL, set it in `meta.json → versions[].sourceUrl`, then in Jellyfin go to **Dashboard → Plugins → Repositories → Add** and paste the repository JSON URL. This is out-of-scope for this README but standard Jellyfin workflow.

### Verifying the install

Open **Dashboard → Plugins → Jellyfin Diagnostics**. Configuration fields should render (AI toggle, endpoint URL, API key, log lines to scan). Click the **Diagnostics** menu entry, then click **Run Diagnostics**. You should see a report with categorized findings within a few seconds.

If nothing appears:
- Check `/config/log/log_*.log` for errors containing `JellyfinDiagnostics` or plugin load failures
- Verify the DLL and `meta.json` are both present and owned by `99:100`
- Verify the plugin folder name includes the version suffix (`_1.0.0.0`)
- Check that Jellyfin is actually version 10.11.x (plugin ABI mismatch will prevent loading)

---

## How the AI integration works

The plugin does **not** run any large language model locally. It is a thin client that sends a sanitized diagnostics report to an **external OpenAI-compatible chat completions endpoint** that you configure.

### The flow

```
Admin clicks "Analyze with AI"
  → POST /Diagnostics/Ai (admin-authenticated)
  → DiagnosticsController checks config: EnableAiIntegration? EndpointUrl set?
  → Retrieves most recent DiagnosticsReport (or runs a fresh scan)
  → AiIntegrationService.SanitizeReport() strips PII:
      • IPv4 addresses → [IP_REDACTED]
      • MAC addresses → [MAC_REDACTED]
      • File paths → [path_1], [path_2], ... (except /dev/*, /proc/*, /config)
      • user=xxx / user:xxx tokens → [USER_REDACTED]
      • OS string → generic "Linux X.Y" only
  → Builds a chat-completions request:
      {
        "model": "default",
        "messages": [
          { "role": "system", "content": "You are a Jellyfin server diagnostics assistant..." },
          { "role": "user", "content": "<sanitized JSON report>" }
        ]
      }
  → POSTs to your configured endpoint with Authorization: Bearer <your_api_key>
  → Response body is returned to the browser and rendered in a modal dialog
```

The plugin is stateless — nothing about the AI exchange is logged or cached server-side. The raw request body on the wire contains only the sanitized report, no Jellyfin config, no user data, no tokens.

### Configuration

In **Dashboard → Plugins → Jellyfin Diagnostics → Settings**:

| Setting | Default | Description |
|---|---|---|
| `EnableAiIntegration` | `false` | Master switch. When `false`, the "Analyze with AI" button is hidden and the `/Diagnostics/Ai` endpoint returns 400 |
| `AiEndpointUrl` | empty | Full URL to the chat completions endpoint (e.g., `https://api.openai.com/v1/chat/completions`) |
| `AiApiKey` | empty | API key sent as `Authorization: Bearer <key>`. Leave empty if your endpoint doesn't require auth |
| `LogLinesToScan` | `5000` | How many recent log lines each checker's LogAnalyzer scans |

### Supported AI backends

Any endpoint that speaks the OpenAI chat completions JSON schema will work:

| Backend | Endpoint URL | Notes |
|---|---|---|
| **OpenAI** | `https://api.openai.com/v1/chat/completions` | Set `AiApiKey` to your OpenAI key |
| **Anthropic (direct)** | Not compatible — requires a shim | Use a proxy like [litellm](https://github.com/BerriAI/litellm) to translate |
| **OpenRouter** | `https://openrouter.ai/api/v1/chat/completions` | Works out of the box, supports Claude/GPT-4/etc via a single API |
| **Ollama (local)** | `http://<unraid-ip>:11434/v1/chat/completions` | Run Ollama in its own Unraid container — AI stays on your LAN, nothing leaves the house |
| **LM Studio / LocalAI** | `http://<host>:<port>/v1/chat/completions` | Same pattern — local inference server |
| **Azure OpenAI** | `https://<resource>.openai.azure.com/openai/deployments/<deployment>/chat/completions?api-version=2024-02-01` | Needs `api-key` header variant — may require shim |

For the most privacy-conscious setup, run **Ollama** in another Unraid Docker container and point the plugin at it. No diagnostics data ever leaves your network.

### What gets sent (example)

A raw finding like:

```json
{
  "category": "Docker Volumes",
  "title": "Library path does not exist: Movies",
  "detail": "The path '/mnt/user/media/movies' for library 'Movies' does not exist inside the container. Server IP 192.168.1.50.",
  "fixSteps": ["chown -R 99:100 /mnt/user/media"]
}
```

becomes, after sanitization:

```json
{
  "category": "Docker Volumes",
  "title": "Library path does not exist: Movies",
  "detail": "The path '[path_1]' for library 'Movies' does not exist inside the container. Server IP [IP_REDACTED].",
  "fixSteps": ["chown -R 99:100 [path_2]"]
}
```

### Model selection

The plugin currently sends `"model": "default"` in the request. Most endpoints will either use their configured default or ignore the field. If you need a specific model, use a proxy like litellm to rewrite it, or fork the plugin and change the `model` string in [Services/AiIntegrationService.cs](Services/AiIntegrationService.cs).

---

## Building

```bash
dotnet build -c Release
```

Output: `bin/Release/net8.0/JellyfinDiagnostics.dll`

The `.csproj` references `Jellyfin.Controller` and `Jellyfin.Model` at version `10.11.*` from NuGet. NuGet resolves these automatically on first `dotnet restore`.

---

## Manual Testing Checklist

The following **must be tested manually** against a real Jellyfin 10.11.6 instance — there is no integration test harness for a Jellyfin plugin.

### Plugin loading
- [ ] Plugin appears in Dashboard → Plugins list after DLL copy + container restart
- [ ] "Diagnostics" entry appears in the admin side menu (from `EnableInMainMenu = true`)
- [ ] Clicking "Diagnostics" loads the embedded HTML page without 404
- [ ] Plugin settings page shows the configuration fields (AI toggle, endpoint URL, API key)

### API endpoints
- [ ] `GET /Diagnostics/Run` returns 200 with JSON report when called with an admin access token
- [ ] Same endpoint returns 401 without admin auth (verifies `RequiresElevation` policy)
- [ ] `GET /Diagnostics/Report/Download` triggers a JSON file download
- [ ] `POST /Diagnostics/Ai` returns 400 when `EnableAiIntegration = false`

### Hardware acceleration detection
- [ ] With HW accel set to `none`, reports "Hardware acceleration is disabled" (Info)
- [ ] With NVENC selected but no NVIDIA runtime, reports "NVIDIA device nodes not found" (Critical)
- [ ] With NVENC + `--runtime=nvidia`, reports device nodes found + `nvidia-smi` available
- [ ] With VAAPI selected and `/dev/dri/renderD128` mapped, reports "renderD128 present" (Info)
- [ ] With VAAPI selected and `/dev/dri` missing, reports "/dev/dri not found" (Critical)
- [ ] FFmpeg encoder check runs against `EncoderAppPath` if set, otherwise `ffmpeg`
- [ ] Detects `h264_nvenc`, `h264_vaapi`, `h264_qsv` from actual `ffmpeg -encoders` output

### Volume & path detection
- [ ] Non-existent library path reports "Library path does not exist" (Critical)
- [ ] Read-only mount reports "Read-only mount detected" (Warning) — test by mapping a volume as `:ro`
- [ ] Library path with UnauthorizedAccessException on `GetFileSystemEntries` reports "not readable" (Critical)
- [ ] Running as UID 99 / GID 100 reports "UID/GID matches Unraid defaults" (Info)
- [ ] Running as 1000:1000 reports "Non-standard UID/GID" (Warning)

### Permissions detection
- [ ] With writable config dir, reports "Config directory is writable" (Info)
- [ ] With read-only config dir (test by `chmod -w`), reports "Config directory is NOT writable" (Critical)
- [ ] Temp test file is cleaned up after every writability check (verify no `.diag_write_test_*` files remain)
- [ ] Permission-denied log pattern triggers a Warning/Critical finding when log contains matching lines

### Network detection
- [ ] HTTPS enabled without cert reports Critical
- [ ] HTTPS enabled with cert file missing reports Critical
- [ ] No `PublishedServerUriBySubnet` entries reports Info with Unraid reverse-proxy suggestion
- [ ] A subnet entry with `localhost` or `127.0.0.1` reports Critical
- [ ] Bitrate cap < 8 Mbps reports Warning

### AI integration (requires OpenAI-compatible endpoint)
- [ ] With `EnableAiIntegration = false`, "Analyze with AI" button is hidden in the UI
- [ ] With AI enabled but no endpoint URL, POST `/Diagnostics/Ai` returns 400 "AI endpoint URL is not configured"
- [ ] With valid endpoint (test against Ollama locally), the AI response renders in a modal
- [ ] Inspect the outbound HTTP request body — sanitized report contains `[IP_REDACTED]`, `[MAC_REDACTED]`, `[path_N]`
- [ ] Sanitized request body does **not** contain: raw file paths, real IPs, API keys, user names, Jellyfin server hostname
- [ ] Modal close button works

### UI / XSS safety
- [ ] All dynamic content uses `textContent`, not `innerHTML` (code review)
- [ ] Rename a library to `<script>alert(1)</script>` — it should render as escaped text in the dashboard, NOT execute

---

## Known limitations

- **Strongly-typed network config binding:** Uses `MediaBrowser.Common.Net.NetworkConfiguration`. If Jellyfin renames properties in a future 10.11.x point release, the checker will need updating.
- **`PublishedServerUriBySubnet` format:** Jellyfin uses `subnet=url` format; the plugin validates URLs but not CIDR correctness.
- **`nvidia-smi` availability:** Depends on the binary being in PATH inside the container. Some images lack it even with working NVIDIA passthrough.
- **No host access:** All checks run from inside the container. Cannot read `/boot/config/` or Unraid syslog directly.
- **FFmpeg subprocess cost:** The `ffmpeg -encoders` check adds 1–3 seconds to each scan.
- **AI model selection:** Hard-codes `"model": "default"` in the request. Many backends ignore this; for specific model routing, use a proxy like litellm.
- **No compile-time verification in this repo:** The build target is Jellyfin 10.11.*, and the .NET SDK was not available on the machine that generated the source. Manual `dotnet build` against a real Jellyfin SDK is required to catch API drift. Report any breakage on the GitHub issues tracker.

## License

MIT
