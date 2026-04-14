# Changelog

## [Unreleased]

- Initial release targeting Jellyfin 10.11.6

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
