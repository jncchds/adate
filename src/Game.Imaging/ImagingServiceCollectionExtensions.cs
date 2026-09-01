using Game.Core.Gpu;
using Game.Imaging.Caching;
using Game.Imaging.Comfy;
using Game.Imaging.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Game.Imaging;

public static class ImagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the imaging stack. Every endpoint is configuration: the image service is
    /// independently addressable and normally does not share a machine with the game.
    /// </summary>
    public static IServiceCollection AddImaging(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.Configure<ComfyOptions>(config.GetSection(ComfyOptions.SectionName));
        services.Configure<WorkflowOptions>(config.GetSection(WorkflowOptions.SectionName));
        services.Configure<ImageStoreOptions>(config.GetSection(ImageStoreOptions.SectionName));

        services.AddHttpClient<IComfyClient, ComfyClient>(static (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ComfyOptions>>().Value;
            http.BaseAddress = Comfy.ComfyClient.EnsureTrailingSlash(options.BaseAddress);

            // Only bounds a single HTTP call. Waiting for a generation to finish is bounded
            // separately by ComfyOptions.GenerationTimeout, because that wait spans many
            // requests plus a websocket and can legitimately run for minutes.
            http.Timeout = options.RequestTimeout;
        });

        services.AddSingleton<IWorkflowRepository, FileWorkflowRepository>();
        services.AddSingleton<IImageStore, FileImageStore>();

        // HANDOFF 1.6. Spike 0 has one GPU consumer, so the lease is a no-op. Which lease is
        // used stays a configuration decision, never a compile-time one.
        services.AddSingleton<IGpuLease, NoOpGpuLease>();

        services.AddSingleton<IImageProvider, ComfyImageProvider>();

        return services;
    }
}
