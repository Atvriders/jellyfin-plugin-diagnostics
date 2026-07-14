using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Api;

[ApiController]
[Route("Diagnostics")]
[Authorize(Policy = "RequiresElevation")]
public class DiagnosticsController : ControllerBase
{
    private readonly DiagnosticsService _diagnosticsService;
    private readonly AiIntegrationService _aiService;
    private readonly HistoryService _historyService;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        DiagnosticsService diagnosticsService,
        AiIntegrationService aiService,
        HistoryService historyService,
        ILogger<DiagnosticsController> logger)
    {
        _diagnosticsService = diagnosticsService;
        _aiService = aiService;
        _historyService = historyService;
        _logger = logger;
    }

    [HttpGet("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDiagnostics(CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var report = await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            await RecordSafelyAsync(
                new HistoryEntry
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
                },
                report,
                cancellationToken).ConfigureAwait(false);

            return Ok(report);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            await RecordSafelyAsync(
                new HistoryEntry
                {
                    Action = ButtonAction.RunDiagnostics,
                    UserName = CurrentUserName(),
                    Outcome = HistoryOutcome.Failed,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = ex.Message
                },
                null,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    [HttpGet("Report")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReport(CancellationToken cancellationToken)
    {
        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(report);
    }

    [HttpGet("Report/Download")]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadReport(CancellationToken cancellationToken)
    {
        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        return File(bytes, MediaTypeNames.Application.Json, "jellyfin-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
    }

    [HttpPost("Ai")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendToAi(CancellationToken cancellationToken)
    {
        var pluginInstance = Plugin.Instance;
        if (pluginInstance == null)
        {
            return BadRequest(new { error = "Plugin not initialized" });
        }

        var config = pluginInstance.Configuration;
        if (!config.EnableAiIntegration)
        {
            return BadRequest(new { error = "AI integration is not enabled. Enable it in plugin settings." });
        }

        if (string.IsNullOrEmpty(config.AiEndpointUrl))
        {
            return BadRequest(new { error = "AI endpoint URL is not configured." });
        }

        // Only a press that actually reached the AI service is a button press worth
        // recording; the guards above are configuration errors, not presses.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var report = _diagnosticsService.GetLastReport()
                         ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);

            var aiResponse = await _aiService.SendToAiAsync(
                report,
                config.AiEndpointUrl,
                config.AiApiKey,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            await RecordSafelyAsync(
                new HistoryEntry
                {
                    Action = ButtonAction.AnalyzeWithAi,
                    UserName = CurrentUserName(),
                    Outcome = HistoryOutcome.Success,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    JellyfinVersion = report.JellyfinVersion,
                    OperatingSystem = report.OperatingSystem
                },
                null,
                cancellationToken).ConfigureAwait(false);

            return Ok(new { response = aiResponse });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            await RecordSafelyAsync(
                new HistoryEntry
                {
                    Action = ButtonAction.AnalyzeWithAi,
                    UserName = CurrentUserName(),
                    Outcome = HistoryOutcome.Failed,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = ex.Message
                },
                null,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

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
        // The id becomes a file name inside the history directory. Reject anything that
        // is not a GUID before it can reach the filesystem.
        if (!Guid.TryParse(id, out _))
        {
            return NotFound();
        }

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

        var saved = await _historyService.RecordAsync(
            new HistoryEntry
            {
                Action = ButtonAction.ExportReport,
                UserName = CurrentUserName(),
                Outcome = HistoryOutcome.Success
            },
            null,
            cancellationToken).ConfigureAwait(false);

        return Ok(saved);
    }

    [HttpDelete("History")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearHistory(CancellationToken cancellationToken)
    {
        await _historyService.ClearAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private string CurrentUserName()
    {
        var name = User?.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return User?.FindFirst("Jellyfin-UserId")?.Value ?? "unknown";
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
}

public class RecordPressRequest
{
    // The dashboard posts {"Action":"ExportReport"} - the enum by name.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ButtonAction Action { get; set; }
}
