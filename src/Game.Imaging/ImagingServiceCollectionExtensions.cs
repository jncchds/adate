using Game.Core.Gpu;
using Game.Imaging.Caching;
using Game.Imaging.Comfy;
using Game.Imaging.Workflows;
using Game.Imaging.ZImage;
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

        services.Configure<ZImageOptions>(config.GetSection(ZImageOptions.SectionName));
        services.AddHttpClient(ZImageImageProvider.HttpClientName, static (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ZImageOptions>>().Value;
            http.BaseAddress = Comfy.ComfyClient.EnsureTrailingSlash(options.BaseAddress);

            // /generate holds the connection open for the whole render, so this bounds a
            // generation rather than a control call.
            http.Timeout = options.RequestTimeout;
        });

        // Which backend renders is configuration, never a compile-time choice. The ComfyUI
        // client stays registered either way: the CLI's stats and free commands talk to it
        // directly.
        var provider = config[ProviderKey] ?? ComfyProvider;
        if (string.Equals(provider, ZImageProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IImageProvider, ZImageImageProvider>();
        }
        else if (string.Equals(provider, ComfyProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IImageProvider, ComfyImageProvider>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown image provider '{provider}' in '{ProviderKey}'. Known: {ComfyProvider}, {ZImageProvider}.");
        }

        return services;
    }

    public const string ProviderKey = "Imaging:Provider";
    public const string ComfyProvider = "Comfy";
    public const string ZImageProvider = "ZImage";
}
