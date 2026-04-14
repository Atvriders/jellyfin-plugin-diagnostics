using JellyfinDiagnostics.Models;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics.Checkers;

public class PluginHealthChecker : IDiagnosticChecker
{
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<PluginHealthChecker> _logger;

    public string Name => "Plugin Health";
    public string Category => "Plugins";

    public PluginHealthChecker(IPluginManager pluginManager, ILogger<PluginHealthChecker> logger)
    {
        _pluginManager = pluginManager;
        _logger = logger;
    }

    public Task<List<DiagnosticResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticResult>();

        try
        {
            var plugins = _pluginManager.Plugins?.ToList() ?? new List<LocalPlugin>();

            var badStatusPlugins = new List<string>();
            var total = plugins.Count;
            var active = 0;
            var deleted = 0;
            var notSupported = 0;
            var malfunctioned = 0;
            var restart = 0;
            var disabled = 0;

            foreach (var plugin in plugins)
            {
                try
                {
                    var status = plugin.Manifest?.Status;
                    switch (status)
                    {
                        case MediaBrowser.Common.Plugins.PluginStatus.Active:
                            active++;
                            break;
                        case MediaBrowser.Common.Plugins.PluginStatus.Deleted:
                            deleted++;
                            badStatusPlugins.Add(plugin.Name + " (Deleted)");
                            break;
                        case MediaBrowser.Common.Plugins.PluginStatus.NotSupported:
                            notSupported++;
                            badStatusPlugins.Add(plugin.Name + " (NotSupported)");
                            break;
                        case MediaBrowser.Common.Plugins.PluginStatus.Malfunctioned:
                            malfunctioned++;
                            badStatusPlugins.Add(plugin.Name + " (Malfunctioned)");
                            break;
                        case MediaBrowser.Common.Plugins.PluginStatus.Restart:
                            restart++;
                            badStatusPlugins.Add(plugin.Name + " (awaiting Restart)");
                            break;
                        case MediaBrowser.Common.Plugins.PluginStatus.Disabled:
                            disabled++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to inspect plugin {PluginName}", plugin.Name);
                }
            }

            results.Add(new DiagnosticResult
            {
                Severity = DiagnosticSeverity.Info,
                Status = DiagnosticStatus.Working,
                Category = Category,
                Title = "Plugin inventory: " + total + " total (" + active + " active)",
                Detail = "Active=" + active + ", NotSupported=" + notSupported + ", Malfunctioned=" + malfunctioned + ", Disabled=" + disabled + ", Deleted=" + deleted + ", NeedsRestart=" + restart,
                UnraidContext = string.Empty,
                FixSteps = new List<string>()
            });

            if (notSupported > 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = notSupported + " plugin(s) marked NotSupported",
                    Detail = "Plugins with NotSupported status:\n" + string.Join("\n", badStatusPlugins.Where(p => p.Contains("NotSupported"))),
                    UnraidContext = "NotSupported means the plugin targets a Jellyfin ABI that does not match the running server. After a Jellyfin upgrade, some plugins need to be updated before they work again.",
                    FixSteps = new List<string>
                    {
                        "Dashboard > Plugins > Catalog > check for updates",
                        "Or visit each plugin's repository for a newer release matching your Jellyfin version",
                        "Remove plugins that have been abandoned"
                    }
                });
            }

            if (malfunctioned > 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Critical,
                    Status = DiagnosticStatus.Broken,
                    Category = Category,
                    Title = malfunctioned + " plugin(s) marked Malfunctioned",
                    Detail = "Plugins that crashed on load:\n" + string.Join("\n", badStatusPlugins.Where(p => p.Contains("Malfunctioned"))),
                    UnraidContext = "A Malfunctioned plugin threw an unhandled exception during initialization and was disabled.",
                    FixSteps = new List<string>
                    {
                        "Check the Jellyfin log for the exception stack trace",
                        "Update or reinstall the plugin",
                        "If it persists, remove the plugin and report to its maintainer"
                    }
                });
            }

            if (restart > 0)
            {
                results.Add(new DiagnosticResult
                {
                    Severity = DiagnosticSeverity.Warning,
                    Status = DiagnosticStatus.Degraded,
                    Category = Category,
                    Title = restart + " plugin(s) need a restart",
                    Detail = "Plugins pending a Jellyfin restart to activate:\n" + string.Join("\n", badStatusPlugins.Where(p => p.Contains("Restart"))),
                    UnraidContext = "These plugins were installed/updated but Jellyfin has not been restarted yet.",
                    FixSteps = new List<string>
                    {
                        "Restart the Jellyfin Docker container from the Unraid Docker tab"
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin health check crashed");
        }

        return Task.FromResult(results);
    }
}
