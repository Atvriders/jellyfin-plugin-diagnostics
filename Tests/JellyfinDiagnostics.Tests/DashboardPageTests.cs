using System.Text.Json;
using Jellyfin.Extensions.Json;
using JellyfinDiagnostics.Models;
using Jint;
using Xunit;

namespace JellyfinDiagnostics.Tests;

/// <summary>
/// Executes the dashboard's own JavaScript, lifted verbatim out of the page that ships
/// inside the plugin assembly, so the client half of the contract is tested rather than
/// re-implemented.
///
/// The bug these exist for: Jellyfin's MVC pipeline serializes enums BY NAME
/// ({"Severity":"Critical"}), but the page compared Severity against 0/1/2. Every
/// comparison was false, so a server with three criticals rendered
/// "0 Critical / 0 Warning / 0 Info" with a green check beside every single finding -
/// while the History row for the same run, counted server-side in C#, said 3.
/// </summary>
public class DashboardPageTests
{
    private const string Resource = "JellyfinDiagnostics.Pages.diagnosticsPage.html";

    private static string PageSource()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(Resource);
        Assert.True(stream != null, $"Embedded page {Resource} is missing from the plugin assembly.");

        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>Lifts a named top-level function out of the page's script, braces balanced.</summary>
    private static string Function(string name)
    {
        var html = PageSource();
        var start = html.IndexOf("function " + name + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The page no longer defines {name}().");

        var open = html.IndexOf('{', start);
        Assert.True(open > start, $"{name}() has no body.");

        var depth = 0;
        for (var i = open; i < html.Length; i++)
        {
            if (html[i] == '{')
            {
                depth++;
            }
            else if (html[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html.Substring(start, i - start + 1);
                }
            }
        }

        Assert.Fail($"{name}() is not brace-balanced.");
        return string.Empty;
    }

    private static Engine SeverityEngine()
    {
        var engine = new Engine();
        engine.Execute(Function("getSev"));
        engine.Execute(Function("severityIcon"));
        return engine;
    }

    /// <summary>
    /// The wire format the page must cope with, proven against Jellyfin's real
    /// serializer options rather than assumed.
    /// </summary>
    [Fact]
    public void Jellyfin_SerializesSeverity_AsAString()
    {
        var json = JsonSerializer.Serialize(
            new DiagnosticResult { Severity = DiagnosticSeverity.Critical, Title = "boom" },
            JsonDefaults.PascalCaseOptions);

        Assert.Contains("\"Severity\":\"Critical\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Critical", 2)]
    [InlineData("Warning", 1)]
    [InlineData("Info", 0)]
    public void GetSev_NormalizesTheStringEnum_JellyfinActuallySends(string name, int expected)
    {
        var engine = SeverityEngine();

        Assert.Equal(expected, (int)engine.Evaluate($"getSev({{ Severity: '{name}' }})").AsNumber());
        Assert.Equal(expected, (int)engine.Evaluate($"getSev({{ severity: '{name}' }})").AsNumber());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(0)]
    public void GetSev_StillAcceptsTheNumericEnum(int value)
    {
        var engine = SeverityEngine();

        Assert.Equal(value, (int)engine.Evaluate($"getSev({{ Severity: {value} }})").AsNumber());
    }

    [Fact]
    public void SeverityIcon_IsRed_ForACriticalFinding_OverTheWire()
    {
        var engine = SeverityEngine();

        Assert.Equal("\u274C", engine.Evaluate("severityIcon(getSev({ Severity: 'Critical' }))").AsString());
        Assert.Equal("\u26A0\uFE0F", engine.Evaluate("severityIcon(getSev({ Severity: 'Warning' }))").AsString());
        Assert.Equal("\u2705", engine.Evaluate("severityIcon(getSev({ Severity: 'Info' }))").AsString());
    }

    /// <summary>
    /// The summary bar and the category icons are both driven by these counts. This is the
    /// exact expression renderReport() uses, over the exact payload the server sends.
    /// </summary>
    [Fact]
    public void SummaryCounts_MatchTheServerSideCounts_ForARealPayload()
    {
        var report = new DiagnosticsReport
        {
            Timestamp = DateTime.UtcNow,
            JellyfinVersion = "10.11.11",
            OperatingSystem = "Linux",
            Results = new List<DiagnosticResult>
            {
                new() { Severity = DiagnosticSeverity.Critical, Title = "a", Category = "Docker Volumes" },
                new() { Severity = DiagnosticSeverity.Critical, Title = "b", Category = "Docker Volumes" },
                new() { Severity = DiagnosticSeverity.Warning, Title = "c", Category = "Network" },
                new() { Severity = DiagnosticSeverity.Info, Title = "d", Category = "Network" }
            }
        };

        var json = JsonSerializer.Serialize(report, JsonDefaults.PascalCaseOptions);

        var engine = SeverityEngine();
        engine.Execute("var report = " + json + ";");
        engine.Execute("var results = report.Results || report.results || [];");

        Assert.Equal(2, (int)engine.Evaluate("results.filter(function (r) { return getSev(r) === 2; }).length").AsNumber());
        Assert.Equal(1, (int)engine.Evaluate("results.filter(function (r) { return getSev(r) === 1; }).length").AsNumber());
        Assert.Equal(1, (int)engine.Evaluate("results.filter(function (r) { return getSev(r) === 0; }).length").AsNumber());

        // The category header icon: "Docker Volumes" must not render as healthy.
        Assert.True(engine.Evaluate(
            "results.filter(function (r) { return r.Category === 'Docker Volumes'; })" +
            ".some(function (f) { return getSev(f) === 2; })").AsBoolean());
    }

    /// <summary>
    /// POST Diagnostics/Ai analyses the server's own current report - never the historical
    /// snapshot the page is displaying. Offering the button while a saved report is on
    /// screen means the AI analyses findings the admin is not looking at (and, after a
    /// restart, silently triggers a whole new scan). It must be hidden.
    /// </summary>
    [Fact]
    public void AiButton_IsHidden_WhileAHistoricalReportIsDisplayed()
    {
        Assert.Equal("none", AiButtonDisplay(viewingHistoricalReport: true, aiEnabled: true));
    }

    [Fact]
    public void AiButton_IsShown_ForALiveReport_WhenAiIsEnabled()
    {
        Assert.Equal("inline-block", AiButtonDisplay(viewingHistoricalReport: false, aiEnabled: true));
        Assert.Equal("none", AiButtonDisplay(viewingHistoricalReport: false, aiEnabled: false));
    }

    private static string AiButtonDisplay(bool viewingHistoricalReport, bool aiEnabled)
    {
        var engine = new Engine();
        engine.Execute("var stubButton = { style: { display: 'unset' } };");
        engine.Execute("var page = { querySelector: function () { return stubButton; } };");
        engine.Execute(
            "var ApiClient = { getPluginConfiguration: function () { return { then: function (cb) { cb({ EnableAiIntegration: " +
            (aiEnabled ? "true" : "false") + " }); return this; } }; } };");
        engine.Execute("var viewingHistoricalReport = " + (viewingHistoricalReport ? "true" : "false") + ";");
        engine.Execute(Function("updateAiButtonVisibility"));
        engine.Execute("updateAiButtonVisibility();");

        return engine.Evaluate("stubButton.style.display").AsString();
    }

    /// <summary>
    /// A single unreadable snapshot (cleared from another tab, say) used to clearNode() the
    /// history host, wiping all 50 rendered rows. A per-row failure must be reported
    /// per-row, not by destroying the list.
    /// </summary>
    [Fact]
    public void ViewHistoryReport_DoesNotDestroyTheHistoryTable_WhenOneReportFails()
    {
        var source = Function("viewHistoryReport");
        var catchIndex = source.IndexOf(".catch", StringComparison.Ordinal);
        Assert.True(catchIndex > 0, "viewHistoryReport() no longer handles a failed load.");

        var failurePath = source.Substring(catchIndex);

        Assert.DoesNotContain("clearNode", failurePath, StringComparison.Ordinal);
        Assert.Contains("historyMessage", failurePath, StringComparison.Ordinal);
        Assert.Contains("loadHistory", failurePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearHistory_DoesNotDestroyTheHistoryTable_WhenTheDeleteFails()
    {
        var source = Function("clearHistory");
        var catchIndex = source.IndexOf(".catch", StringComparison.Ordinal);
        Assert.True(catchIndex > 0, "clearHistory() no longer handles a failed delete.");

        Assert.DoesNotContain("clearNode", source.Substring(catchIndex), StringComparison.Ordinal);
    }

    /// <summary>The page renders untrusted server strings; innerHTML stays banned.</summary>
    [Fact]
    public void Page_NeverUsesInnerHtml()
    {
        Assert.DoesNotContain("innerHTML", PageSource(), StringComparison.Ordinal);
    }
}
