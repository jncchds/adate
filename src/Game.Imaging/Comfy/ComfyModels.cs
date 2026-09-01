using System.Text.Json.Serialization;

namespace Game.Imaging.Comfy;

/// <summary>An image as ComfyUI addresses it in <c>/history</c> and <c>/view</c>.</summary>
public sealed record ComfyImageRef(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("subfolder")] string Subfolder,
    [property: JsonPropertyName("type")] string Type);

/// <summary>Response from <c>POST /prompt</c>.</summary>
public sealed record QueuedPrompt(
    [property: JsonPropertyName("prompt_id")] string PromptId,
    [property: JsonPropertyName("number")] int Number);

/// <summary>Response from <c>POST /upload/image</c>.</summary>
public sealed record UploadedImage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subfolder")] string Subfolder,
    [property: JsonPropertyName("type")] string Type);

/// <summary>
/// Subset of <c>GET /system_stats</c>. Used by the CLI harness to answer the HANDOFF 8
/// step 1 question — is there VRAM headroom — without a human reading the ComfyUI UI.
/// </summary>
public sealed record ComfySystemStats(
    [property: JsonPropertyName("system")] ComfySystemInfo? System,
    [property: JsonPropertyName("devices")] IReadOnlyList<ComfyDeviceInfo>? Devices);

public sealed record ComfySystemInfo(
    [property: JsonPropertyName("comfyui_version")] string? ComfyUIVersion,
    [property: JsonPropertyName("python_version")] string? PythonVersion);

public sealed record ComfyDeviceInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("vram_total")] long VramTotal,
    [property: JsonPropertyName("vram_free")] long VramFree);

/// <summary>Progress notification emitted while a prompt executes.</summary>
public sealed record ComfyProgress(string PromptId, string? NodeId, int Value, int Max);
