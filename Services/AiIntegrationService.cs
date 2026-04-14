using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinDiagnostics.Models;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Services;

public class AiIntegrationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiIntegrationService> _logger;

    public AiIntegrationService(
        IHttpClientFactory httpClientFactory,
        ILogger<AiIntegrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public DiagnosticsReport SanitizeReport(DiagnosticsReport report)
    {
        var sanitized = new DiagnosticsReport
        {
            Timestamp = report.Timestamp,
            JellyfinVersion = report.JellyfinVersion,
            OperatingSystem = SanitizeOsString(report.OperatingSystem),
            Results = new List<DiagnosticResult>()
        };

        var ctx = new SanitizeContext();

        foreach (var result in report.Results)
        {
            sanitized.Results.Add(new DiagnosticResult
            {
                Severity = result.Severity,
                Status = result.Status,
                Category = result.Category,
                Title = SanitizeText(result.Title, ctx),
                Detail = SanitizeText(result.Detail, ctx),
                UnraidContext = SanitizeText(result.UnraidContext, ctx),
                FixSteps = result.FixSteps.Select(s => SanitizeText(s, ctx)).ToList()
            });
        }

        return sanitized;
    }

    private sealed class SanitizeContext
    {
        public int PathCounter;
        public Dictionary<string, string> PathMap { get; } = new();
    }

    public async Task<string> SendToAiAsync(
        DiagnosticsReport report,
        string endpointUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var sanitized = SanitizeReport(report);
        var client = _httpClientFactory.CreateClient("DiagnosticsAi");

        var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        request.Content = JsonContent.Create(new
        {
            model = "default",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a Jellyfin server diagnostics assistant. Analyze the following diagnostics report from a Jellyfin server running in Docker on Unraid. Provide clear, actionable recommendations. Do not ask for additional information."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(sanitized, new JsonSerializerOptions { WriteIndented = true })
                }
            }
        });

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Add("Authorization", "Bearer " + apiKey);
        }

        try
        {
            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send diagnostics to AI endpoint");
            throw;
        }
    }

    private static string SanitizeText(string text, SanitizeContext ctx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = Regex.Replace(text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[IP_REDACTED]");
        text = Regex.Replace(text, @"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", "[MAC_REDACTED]");

        text = Regex.Replace(text, @"(/[\w./-]+)", match =>
        {
            var path = match.Value;
            if (path.StartsWith("/dev/") || path.StartsWith("/proc/") || path == "/config")
            {
                return path;
            }

            if (!ctx.PathMap.TryGetValue(path, out var replacement))
            {
                ctx.PathCounter++;
                replacement = "[path_" + ctx.PathCounter + "]";
                ctx.PathMap[path] = replacement;
            }

            return replacement;
        });

        text = Regex.Replace(text, @"\buser[=:]\s*\w+", "[USER_REDACTED]");

        return text;
    }

    private static string SanitizeOsString(string os)
    {
        var match = Regex.Match(os, @"(Linux|Windows|Darwin|FreeBSD)\s+[\d.]+");
        return match.Success ? match.Value : "Linux (version redacted)";
    }
}
