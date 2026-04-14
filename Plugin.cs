using JellyfinDiagnostics.Checkers;
using JellyfinDiagnostics.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

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
        return new[]
        {
            new PluginPageInfo
            {
                Name = "JellyfinDiagnostics",
                EmbeddedResourcePath = GetType().Namespace + ".Pages.diagnosticsPage.html",
                EnableInMainMenu = true,
                DisplayName = "Diagnostics"
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

        serviceCollection.AddSingleton<IDiagnosticChecker, HardwareAccelerationChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, VolumePathChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, PermissionsChecker>();
        serviceCollection.AddSingleton<IDiagnosticChecker, NetworkChecker>();
    }
}
