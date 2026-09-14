using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Game.App;

/// <summary>The addresses a player may change: where the language model and the image service run.</summary>
public sealed record ServerSettings(string LlmAddress, string LlmModel, string EmbeddingModel, string ImageAddress)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static ServerSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new(
            configuration["Llm:BaseAddress"] ?? "",
            configuration["Llm:Model"] ?? "",
            configuration["Llm:EmbeddingModel"] ?? "",
            configuration["ZImage:BaseAddress"] ?? "");
    }

    /// <summary>Writes the player's settings file, which is layered over the shipped defaults.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var json = new JsonObject
        {
            ["Llm"] = new JsonObject
            {
                ["BaseAddress"] = LlmAddress.Trim(),
                ["Model"] = LlmModel.Trim(),
                ["EmbeddingModel"] = EmbeddingModel.Trim(),
            },
            ["ZImage"] = new JsonObject { ["BaseAddress"] = ImageAddress.Trim() },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(Indented));
    }
}
