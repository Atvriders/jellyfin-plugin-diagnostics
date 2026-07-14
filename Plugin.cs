using JellyfinDiagnostics.Checkers;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyfinDiagnostics;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "Jellyfin Diagnostics";
    public override string Description => "Admin diagnostics for Docker/Unraid Jellyfin deployments";
    public override Guid Id => new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        return new[]
        {
            // Settings page: opened when admin clicks "Settings" next to the plugin
            // in Dashboard -> Plugins. Name must match the plugin class name.
            new PluginPageInfo
            {
                Name = "JellyfinDiagnostics",
                EmbeddedResourcePath = prefix + ".Pages.configPage.html"
            },
            // Dashboard page: shown in the admin side menu as "Diagnostics".
            new PluginPageInfo
            {
                Name = "JellyfinDiagnosticsDashboard",
                EmbeddedResourcePath = prefix + ".Pages.diagnosticsPage.html",
                EnableInMainMenu = true,
                DisplayName = "Diagnostics",
                MenuSection = "server",
                MenuIcon = "troubleshoot"
            }
        };
    }
}

public class DiagnosticsPluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LogAnalyzer>();
        serviceCollection.AddSingleton<DiagnosticsService>();
        serviceCollection.AddSingleton<AiIntegrationService>();

        // HistoryService takes a plain string root (so it stays unit-testable), which
        // means it needs a factory rather than plain AddSingleton<T>().
        serviceCollection.AddSingleton<HistoryService>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<HistoryService>>();
            var root = Path.Combine(paths.DataPath, "diagnostics-history");
            return new HistoryService(root, logger);
        });

        serviceCollection.AddSingleton<IDiagnosticChecker, HardwareAccelerationChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, VolumePathChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, PermissionsChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, NetworkChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, DatabaseHealthChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, PerformanceChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, ContainerResourceChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, ScheduledTasksChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, PluginHealthChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, SecurityChecker>();
    }
}
