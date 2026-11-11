using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.GlideToolkit.Core;

namespace Jellyfin.Plugin.GlideToolkit;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;
    public string[] AllowedUserIds { get; set; } = [];
    /// Empty means library roots; a nonempty list further restricts those roots.
    public string[] AllowedRoots { get; set; } = [];
}
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { Instance = this; }
    public override string Name => "Glide Video Toolkit";
    public override string Description => "Server-side lossless video rotation, durable jobs and exact backup restore for Glide.";
    public override Guid Id => Guid.Parse("9e405477-411c-4c2b-8541-c3b5925f7169");
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "GlideToolkit", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" }];
}
public sealed class Services : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<RotationLibrary>();
        services.AddSingleton<RotationStore>(provider => new RotationStore(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "glide-rotation-jobs")));
        services.AddSingleton<RotationBatches>(provider => new RotationBatches(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "glide-rotation-batches")));
        services.AddSingleton<RotationPreviews>();
        services.AddHostedService<RotationBatchDispatcher>();
        services.AddSingleton<RotationProcessor>();
        services.AddSingleton<RotationJobs>();
        services.AddHostedService(provider => provider.GetRequiredService<RotationJobs>());
    }
}
