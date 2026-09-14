namespace Game.Imaging.ZImage;

public sealed class ZImageOptions
{
    public const string SectionName = "ZImage";

    /// <summary>
    /// Base address of the Z-Image API. Like ComfyUI, it is independently addressable and
    /// normally lives on the GPU box rather than next to the game.
    /// </summary>
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8000");

    /// <summary>
    /// Bounds a whole generation, not just a control call: <c>/generate</c> holds the request
    /// open until the image exists. Generous because the first call after a container start
    /// also pays for Triton compiling its kernels.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Turbo is distilled for 8 steps at cfg 1.0. Both enter the cache key, because the same
    /// prompt and seed at a different step count is a different image.
    /// </summary>
    public int Steps { get; set; } = 8;

    public double Cfg { get; set; } = 1.0;

    /// <summary>
    /// Workflow ids whose output is composited over a background and so must come back with
    /// alpha. The server mattes them; the game never does (HANDOFF 6). Set in configuration as a
    /// whole list rather than appended to: binding a list onto a non-empty default appends.
    /// </summary>
    public string[] MatteWorkflows { get; set; } = ["zimage-sprite"];

    /// <summary>
    /// The matting model the server runs, as it reports it. Enters the cache key for matted
    /// images only: a sprite cut out by a different model is a different image, but a
    /// background does not change when the matting model does.
    /// </summary>
    public string MattingModel { get; set; } = "ZhengPeng7/BiRefNet";
}
