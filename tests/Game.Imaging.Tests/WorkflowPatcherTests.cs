using System.Text.Json.Nodes;
using Game.Imaging.Workflows;

namespace Game.Imaging.Tests;

public class WorkflowPatcherTests
{
    /// <summary>A minimal stand-in for a ComfyUI API-format export.</summary>
    private static JsonObject Graph() => JsonNode.Parse(
        """
        {
          "3": { "class_type": "KSampler", "inputs": { "seed": 0, "steps": 20, "cfg": 7.0 } },
          "5": { "class_type": "EmptyLatentImage", "inputs": { "width": 512, "height": 512 } },
          "6": { "class_type": "CLIPTextEncode", "inputs": { "text": "", "clip": ["4", 1] } },
          "7": { "class_type": "CLIPTextEncode", "inputs": { "text": "", "clip": ["4", 1] } },
          "9": { "class_type": "SaveImage", "inputs": { "images": ["8", 0] } }
        }
        """)!.AsObject();

    private static WorkflowManifest Manifest() => new()
    {
        Id = "portrait",
        WorkflowFile = "portrait.json",
        Inputs = new Dictionary<string, string>
        {
            [WorkflowInputs.Positive] = "6.inputs.text",
            [WorkflowInputs.Negative] = "7.inputs.text",
            [WorkflowInputs.Seed] = "3.inputs.seed",
            [WorkflowInputs.Width] = "5.inputs.width",
            [WorkflowInputs.Height] = "5.inputs.height",
        },
        OutputNodes = ["9"],
    };

    [Fact]
    public void Patches_every_mapped_input()
    {
        var patched = WorkflowPatcher.Patch(Graph(), Manifest(), new Dictionary<string, object?>
        {
            [WorkflowInputs.Positive] = "1girl, blue eyes",
            [WorkflowInputs.Negative] = "lowres",
            [WorkflowInputs.Seed] = 987654321L,
            [WorkflowInputs.Width] = 768,
            [WorkflowInputs.Height] = 1152,
        });

        Assert.Equal("1girl, blue eyes", (string?)patched["6"]!["inputs"]!["text"]);
        Assert.Equal("lowres", (string?)patched["7"]!["inputs"]!["text"]);
        Assert.Equal(987654321L, (long?)patched["3"]!["inputs"]!["seed"]);
        Assert.Equal(768, (int?)patched["5"]!["inputs"]!["width"]);
        Assert.Equal(1152, (int?)patched["5"]!["inputs"]!["height"]);
    }

    /// <summary>
    /// The repository caches one template per workflow and hands it to every request, so a
    /// patch that mutated it would leak one generation's prompt into the next.
    /// </summary>
    [Fact]
    public void Does_not_mutate_the_template()
    {
        var template = Graph();

        WorkflowPatcher.Patch(template, Manifest(), new Dictionary<string, object?>
        {
            [WorkflowInputs.Positive] = "leaked",
        });

        Assert.Equal(string.Empty, (string?)template["6"]!["inputs"]!["text"]);
    }

    /// <summary>
    /// A background graph has no anchor slot. Supplying one must be ignored rather than
    /// throwing, so a single provider can drive graphs of differing capability.
    /// </summary>
    [Fact]
    public void Ignores_inputs_the_workflow_does_not_declare()
    {
        var patched = WorkflowPatcher.Patch(Graph(), Manifest(), new Dictionary<string, object?>
        {
            [WorkflowInputs.Anchor] = "anchor.png",
        });

        Assert.False(patched.ContainsKey("12"));
    }

    [Fact]
    public void Ignores_null_values()
    {
        var patched = WorkflowPatcher.Patch(Graph(), Manifest(), new Dictionary<string, object?>
        {
            [WorkflowInputs.Positive] = null,
        });

        Assert.Equal(string.Empty, (string?)patched["6"]!["inputs"]!["text"]);
    }

    /// <summary>
    /// The manifest and the graph drifting apart is the most likely mistake when a workflow
    /// is re-exported from the ComfyUI UI, and it must fail loudly rather than silently
    /// producing an unpatched generation.
    /// </summary>
    [Fact]
    public void Throws_when_the_mapped_node_is_missing()
    {
        var manifest = Manifest() with
        {
            Inputs = new Dictionary<string, string> { [WorkflowInputs.Positive] = "99.inputs.text" },
        };

        var ex = Assert.Throws<WorkflowPatchException>(() =>
            WorkflowPatcher.Patch(Graph(), manifest, new Dictionary<string, object?>
            {
                [WorkflowInputs.Positive] = "x",
            }));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throws_when_the_path_is_too_short_to_address_a_property()
    {
        var manifest = Manifest() with
        {
            Inputs = new Dictionary<string, string> { [WorkflowInputs.Positive] = "6" },
        };

        Assert.Throws<WorkflowPatchException>(() =>
            WorkflowPatcher.Patch(Graph(), manifest, new Dictionary<string, object?>
            {
                [WorkflowInputs.Positive] = "x",
            }));
    }

    [Fact]
    public void Writes_doubles_without_locale_dependent_formatting()
    {
        var manifest = Manifest() with
        {
            Inputs = new Dictionary<string, string> { [WorkflowInputs.Cfg] = "3.inputs.cfg" },
        };

        var patched = WorkflowPatcher.Patch(Graph(), manifest, new Dictionary<string, object?>
        {
            [WorkflowInputs.Cfg] = 7.5,
        });

        Assert.Equal(7.5, (double?)patched["3"]!["inputs"]!["cfg"]);
        Assert.DoesNotContain(",", patched["3"]!["inputs"]!["cfg"]!.ToJsonString(), StringComparison.Ordinal);
    }
}
