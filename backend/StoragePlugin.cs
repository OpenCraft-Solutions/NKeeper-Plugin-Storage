using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Storage;

/// <summary>
/// The storage plugin (docs/architecture.md §7.27): a Linux host's shared storage (iSCSI, multipath and DRBD) on the
/// ssh plugin's <c>ssh</c> services. Core opens the connection, checks the server's key and signs in; this plugin
/// only runs the host's own tools.
/// </summary>
public sealed class StoragePlugin : IPlugin
{
    /// <summary>The ssh plugin's service type, which the tool opens on.</summary>
    public const string SshServiceType = "ssh";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddSessionType(new SessionType("storage", "Storage", typeof(StorageSessionHandler)) { ServiceTypes = [SshServiceType] });
        contributions.AddHostAction(new HostAction("storage.open", "Storage", SessionType: "storage"));
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddScoped<StorageSessionHandler>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
