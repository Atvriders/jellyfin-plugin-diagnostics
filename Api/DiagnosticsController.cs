using System.Net.Mime;
using System.Text.Json;
using JellyfinDiagnostics.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinDiagnostics.Api;

[ApiController]
[Route("Diagnostics")]
[Authorize(Policy = "RequiresElevation")]
public class DiagnosticsController : ControllerBase
{
    private readonly DiagnosticsService _diagnosticsService;
    private readonly AiIntegrationService _aiService;

    public DiagnosticsController(
        DiagnosticsService diagnosticsService,
        AiIntegrationService aiService)
    {
        _diagnosticsService = diagnosticsService;
        _aiService = aiService;
    }

    [HttpGet("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunDiagnostics(CancellationToken cancellationToken)
    {
        var report = await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(report);
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

        var report = _diagnosticsService.GetLastReport()
                     ?? await _diagnosticsService.RunAllAsync(cancellationToken).ConfigureAwait(false);

        var aiResponse = await _aiService.SendToAiAsync(
            report,
            config.AiEndpointUrl,
            config.AiApiKey,
            cancellationToken).ConfigureAwait(false);

        return Ok(new { response = aiResponse });
    }
}
