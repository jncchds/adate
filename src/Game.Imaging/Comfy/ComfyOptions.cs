namespace Game.Imaging.Comfy;

public sealed class ComfyOptions
{
    public const string SectionName = "Comfy";

    /// <summary>
    /// Base address of the ComfyUI server. Not necessarily localhost: the image service is
    /// independently addressable and normally lives on a different machine from the game.
    /// The server must have been started with <c>--listen</c> to accept non-local traffic.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8188");

    /// <summary>Timeout for short control calls (queue, history, upload, stats).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for a queued prompt to finish. Generous by default: the job may sit
    /// behind other work in the queue, and a cold model load costs tens of seconds.
    /// </summary>
    public TimeSpan GenerationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fallback poll interval for <c>/history</c>. Completion normally arrives over the
    /// websocket; this only matters when the socket drops mid-generation.
    /// </summary>
    public TimeSpan HistoryPollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
