using MediaBrowser.Model.Plugins;

namespace JellyfinDiagnostics;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableAiIntegration { get; set; } = false;
    public string AiEndpointUrl { get; set; } = string.Empty;
    public string AiApiKey { get; set; } = string.Empty;
    public int LogLinesToScan { get; set; } = 5000;
}
