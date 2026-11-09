using System.Text.Json;
using System.Text.Json.Nodes;
using Game.Llm;
using Microsoft.Extensions.Configuration;

namespace Game.Host.Services;

/// <summary>
/// What the settings page edits: the language model and the image service. They are saved to
/// <see cref="FilePath"/>, which Program.cs layers over appsettings, so a change shows after a restart
/// (the HTTP clients read their addresses once).
/// </summary>
public sealed record HostSettings(
    LlmProviderType LlmProvider,
    string LlmAddress,
    string LlmApiKey,
    string LlmModel,
    string EmbeddingModel,
    string ReasoningEffort,
    string ImageAddress)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The player's settings file, next to the database in the working directory's <c>data</c> folder.</summary>
    public static string FilePath { get; } = Path.Combine(Directory.GetCurrentDirectory(), "data", "settings.json");

    public static HostSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new(
            LlmServiceCollectionExtensions.ProviderFrom(configuration),
            configuration["Llm:BaseAddress"] ?? "",
            configuration["Llm:ApiKey"] ?? "",
            configuration["Llm:Model"] ?? "",
            configuration["Llm:EmbeddingModel"] ?? "",
            configuration["Llm:ReasoningEffort"] ?? "",
            configuration["ZImage:BaseAddress"] ?? "");
    }

    /// <summary>Checks the values the way the game will need them; throws a message fit to show.</summary>
    public void Validate()
    {
        var label = LlmProviders.Label(LlmProvider);

        if ((!string.IsNullOrWhiteSpace(LlmAddress) && !Uri.TryCreate(LlmAddress.Trim(), UriKind.Absolute, out _))
            || (!string.IsNullOrWhiteSpace(ImageAddress) && !Uri.TryCreate(ImageAddress.Trim(), UriKind.Absolute, out _)))
        {
            throw new ArgumentException("Addresses must be full URLs, such as http://192.168.2.33:1234/v1/. Leave the language model's empty for the provider's default.");
        }

        if (LlmProviders.NeedsApiKey(LlmProvider) && string.IsNullOrWhiteSpace(LlmApiKey))
        {
            throw new ArgumentException($"{label} needs an API key.");
        }

        if (LlmProviders.NeedsModel(LlmProvider) && string.IsNullOrWhiteSpace(LlmModel))
        {
            throw new ArgumentException($"{label} needs a scene model.");
        }
    }

    public void Save()
    {
        Validate();

        var llm = new JsonObject
        {
            ["Provider"] = LlmProvider.ToString(),
            ["BaseAddress"] = LlmAddress.Trim(),
            ["ApiKey"] = LlmApiKey.Trim(),
            ["Model"] = LlmModel.Trim(),
            ["EmbeddingModel"] = EmbeddingModel.Trim(),
        };

        // An empty effort would override a configured one with "", so leave it out and let the default stand.
        if (!string.IsNullOrWhiteSpace(ReasoningEffort))
        {
            llm["ReasoningEffort"] = ReasoningEffort.Trim();
        }

        var json = new JsonObject
        {
            ["Llm"] = llm,
            ["ZImage"] = new JsonObject { ["BaseAddress"] = ImageAddress.Trim() },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json.ToJsonString(Indented));
    }

    /// <summary>Removes the player's file, so the shipped defaults apply again after a restart.</summary>
    public static void Reset()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
