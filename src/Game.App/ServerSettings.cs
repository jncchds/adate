using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Llm;
using Microsoft.Extensions.Configuration;

namespace Game.App;

/// <summary>
/// What a player may change about the servers: which language model provider the game writes with and
/// where it is, and where the image service runs. An empty LLM address means the provider's default.
/// </summary>
public sealed record ServerSettings(
    LlmProviderType LlmProvider,
    string LlmAddress,
    string LlmApiKey,
    string LlmModel,
    string EmbeddingModel,
    string ImageAddress)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static ServerSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new(
            LlmServiceCollectionExtensions.ProviderFrom(configuration),
            configuration["Llm:BaseAddress"] ?? "",
            configuration["Llm:ApiKey"] ?? "",
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
                ["Provider"] = LlmProvider.ToString(),
                ["BaseAddress"] = LlmAddress.Trim(),
                ["ApiKey"] = LlmApiKey.Trim(),
                ["Model"] = LlmModel.Trim(),
                ["EmbeddingModel"] = EmbeddingModel.Trim(),
            },
            ["ZImage"] = new JsonObject { ["BaseAddress"] = ImageAddress.Trim() },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(Indented));
    }
}
