using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinDiagnostics.Api;
using JellyfinDiagnostics.Models;
using JellyfinDiagnostics.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyfinDiagnostics.Tests;

/// <summary>
/// Pins the wire format of POST Diagnostics/History/Record.
///
/// The dashboard page and the controller were written independently: the page posts an
/// enum BY NAME ({"Action":"ExportReport"}) while the C# enum's underlying value is 1.
/// If RecordPressRequest.Action ever loses its JsonStringEnumConverter, the body stops
/// binding and every Export press is silently dropped (or 400s) in production -- with no
/// visible symptom, because the page fires the call best-effort and swallows failures.
///
/// So these tests do NOT hardcode a copy of the request body. They read the ACTUAL page
/// that ships inside the plugin assembly, extract the literal body it posts, and push it
/// through the real controller. Either half drifting fails the build.
/// </summary>
public class RecordPressContractTests : IDisposable
{
    private readonly string _root;
    private readonly HistoryService _history;
    private readonly DiagnosticsController _controller;

    public RecordPressContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jfdiag-contract-" + Guid.NewGuid().ToString("N"));
        _history = new HistoryService(_root, NullLogger<HistoryService>.Instance);

        // Only the history service participates in this endpoint; the scan/AI services are
        // never touched on the Record path, so they stay null on purpose. If a future edit
        // makes RecordPress depend on them, these tests will NRE loudly rather than pass a
        // path that was never really exercised.
        _controller = new DiagnosticsController(
            null!,
            null!,
            _history,
            NullLogger<DiagnosticsController>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Pulls the exact JSON body the dashboard posts to History/Record out of the embedded
    /// page, converting the JS object literal it is built from into the JSON that
    /// JSON.stringify would emit at runtime.
    /// </summary>
    private static string PageExportPressBody()
    {
        const string Resource = "JellyfinDiagnostics.Pages.diagnosticsPage.html";

        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Resource);
        Assert.True(stream != null, $"Embedded page {Resource} is missing from the plugin assembly.");

        using var reader = new StreamReader(stream!);
        var html = reader.ReadToEnd();

        var routeIndex = html.IndexOf("Diagnostics/History/Record", StringComparison.Ordinal);
        Assert.True(routeIndex > 0, "The page no longer posts to Diagnostics/History/Record.");

        // The body literal sits just after the url in the same ajax() call.
        var window = html.Substring(routeIndex, Math.Min(400, html.Length - routeIndex));

        var body = Regex.Match(window, @"JSON\.stringify\(\s*\{(?<pairs>[^}]*)\}\s*\)");
        Assert.True(body.Success, "The page no longer sends a JSON.stringify'd object to History/Record.");

        var pair = Regex.Match(body.Groups["pairs"].Value.Trim(), @"^(?<key>\w+)\s*:\s*'(?<value>\w+)'$");
        Assert.True(pair.Success, $"Unexpected request body shape: {body.Groups["pairs"].Value.Trim()}");

        return $"{{\"{pair.Groups["key"].Value}\":\"{pair.Groups["value"].Value}\"}}";
    }

    // ASP.NET may be configured PascalCase (Jellyfin's own default) or camelCase/web
    // defaults. A property-level [JsonConverter] is honoured under both, and the binding
    // must not silently depend on which one the host happens to install.
    public static TheoryData<JsonSerializerOptions> HostOptions => new()
    {
        new JsonSerializerOptions(),
        new JsonSerializerOptions(JsonSerializerDefaults.Web),
    };

    [Fact]
    public void Page_PostsTheEnumByName()
    {
        Assert.Equal("{\"Action\":\"ExportReport\"}", PageExportPressBody());
    }

    [Theory]
    [MemberData(nameof(HostOptions))]
    public void PageBody_DeserializesTo_ExportReport(JsonSerializerOptions options)
    {
        var request = JsonSerializer.Deserialize<RecordPressRequest>(PageExportPressBody(), options);

        Assert.NotNull(request);
        Assert.Equal(ButtonAction.ExportReport, request!.Action);
    }

    [Fact]
    public async Task Controller_Accepts_TheExactBodyThePageSends_AndRecordsIt()
    {
        var request = JsonSerializer.Deserialize<RecordPressRequest>(PageExportPressBody());

        var result = await _controller.RecordPress(request!, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var saved = Assert.IsType<HistoryEntry>(ok.Value);
        Assert.Equal(ButtonAction.ExportReport, saved.Action);
        Assert.Equal(HistoryOutcome.Success, saved.Outcome);

        // And it is actually durable, not just echoed back.
        var history = await _history.GetHistoryAsync(0, 50, CancellationToken.None);
        Assert.Equal(ButtonAction.ExportReport, Assert.Single(history).Action);
    }

    [Theory]
    [InlineData("{\"Action\":\"RunDiagnostics\"}")]
    [InlineData("{\"Action\":\"AnalyzeWithAi\"}")]
    public async Task Controller_Rejects_ServerSideActions(string body)
    {
        // Run and AI outcomes are recorded server-side from what actually happened. A client
        // that could assert them could forge scan results into the history.
        var request = JsonSerializer.Deserialize<RecordPressRequest>(body);

        var result = await _controller.RecordPress(request!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _history.GetHistoryAsync(0, 50, CancellationToken.None));
    }

    [Fact]
    public async Task Controller_Rejects_EmptyBody()
    {
        // RunDiagnostics is 0, so a missing Action defaults into it. That must fail closed.
        var request = JsonSerializer.Deserialize<RecordPressRequest>("{}");

        var result = await _controller.RecordPress(request!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _history.GetHistoryAsync(0, 50, CancellationToken.None));
    }

    [Fact]
    public async Task Controller_Rejects_NullBody()
    {
        var result = await _controller.RecordPress(null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
