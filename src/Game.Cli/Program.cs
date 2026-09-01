using Game.Core;
using Game.Imaging;
using Game.Imaging.Comfy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// HANDOFF 8 step 3: prove the ComfyUI client from a console app -- submit, await the
// websocket, fetch bytes -- before any of it goes near Blazor.
//
//   dotnet run --project src/Game.Cli -- stats
//   dotnet run --project src/Game.Cli -- generate portrait "1girl, blue eyes" --seed 42
//   dotnet run --project src/Game.Cli -- free

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables("ADATE_");
builder.Services.AddImaging(builder.Configuration);
builder.Logging.SetMinimumLevel(LogLevel.Information);

// The HttpClient logging handler dumps the full socket stack trace on a refused
// connection, which buries the actionable message this harness prints instead.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

using var host = builder.Build();
var console = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("cli");

var command = args.FirstOrDefault() ?? "help";

try
{
    return command switch
    {
        "stats" => await StatsAsync(host.Services),
        "generate" => await GenerateAsync(host.Services, args),
        "free" => await FreeAsync(host.Services),
        _ => Help(),
    };
}
catch (ComfyException ex)
{
    console.LogError("{Message}", ex.Message);
    if (ex is ComfyExecutionException { Traceback.Count: > 0 } exec)
    {
        console.LogError("ComfyUI traceback:\n{Traceback}", string.Join('\n', exec.Traceback));
    }

    return 1;
}
catch (HttpRequestException ex)
{
    // By far the most common failure while bringing the GPU box up, and a stack trace says
    // nothing useful about it. ComfyUI binds to loopback unless started with --listen, so a
    // refused connection from another machine usually means the flag is missing rather than
    // that the server is down.
    var endpoint = host.Services
        .GetRequiredService<IOptions<ComfyOptions>>().Value.BaseAddress;

    console.LogError(
        "Could not reach ComfyUI at {Endpoint}: {Message}\n" +
        "Check that the server is running, that it was started with --listen so it accepts " +
        "non-local connections, and that ADATE_Comfy__BaseAddress points at it.",
        endpoint,
        ex.Message);

    return 1;
}
catch (Exception ex)
{
    console.LogError(ex, "Unhandled failure.");
    return 1;
}

// Answers HANDOFF 8 step 1 -- "confirm VRAM headroom" -- without a human reading the
// ComfyUI web UI, and doubles as a reachability check for the configured endpoint.
static async Task<int> StatsAsync(IServiceProvider services)
{
    var comfy = services.GetRequiredService<IComfyClient>();
    var stats = await comfy.GetSystemStatsAsync();

    Console.WriteLine($"ComfyUI  : {stats.System?.ComfyUIVersion ?? "unknown"}");
    Console.WriteLine($"Python   : {stats.System?.PythonVersion ?? "unknown"}");

    foreach (var device in stats.Devices ?? [])
    {
        var totalGb = device.VramTotal / 1024d / 1024d / 1024d;
        var freeGb = device.VramFree / 1024d / 1024d / 1024d;
        Console.WriteLine(
            $"Device   : {device.Name} [{device.Type}] {freeGb:0.00} GB free of {totalGb:0.00} GB");
    }

    return 0;
}

static async Task<int> GenerateAsync(IServiceProvider services, string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: generate <workflowId> <positive> [--negative t] [--seed n] [--size WxH]");
        return 2;
    }

    var provider = services.GetRequiredService<IImageProvider>();

    var seed = ArgValue(args, "--seed") is { } s ? long.Parse(s) : Random.Shared.NextInt64(0, int.MaxValue);
    var size = (ArgValue(args, "--size") ?? "512x768").Split('x');

    var request = new ImageRequest(
        WorkflowId: args[1],
        Positive: args[2],
        Negative: ArgValue(args, "--negative") ?? "lowres, worst quality, blurry",
        Seed: seed,
        Width: int.Parse(size[0]),
        Height: int.Parse(size[1]),
        // Spike 0 has no pack loader wired into the CLI yet; a literal keeps cache entries
        // from this harness separate from anything the game generates.
        PackFingerprint: "cli-harness",
        AnchorImageHash: ArgValue(args, "--anchor"),
        AnchorWeight: ArgValue(args, "--anchor-weight") is { } w ? double.Parse(w) : null,
        Ceiling: Ceiling.PG13);

    var started = DateTimeOffset.UtcNow;
    var image = await provider.GenerateAsync(request);
    var elapsed = DateTimeOffset.UtcNow - started;

    Console.WriteLine($"hash     : {image.Hash}");
    Console.WriteLine($"path     : {image.RelativePath}");
    Console.WriteLine($"seed     : {seed}");
    Console.WriteLine($"cached   : {image.FromCache}");
    Console.WriteLine($"elapsed  : {elapsed.TotalSeconds:0.0}s");

    return 0;
}

static async Task<int> FreeAsync(IServiceProvider services)
{
    await services.GetRequiredService<IComfyClient>().FreeAsync();
    Console.WriteLine("Requested model unload and memory free.");
    return 0;
}

static string? ArgValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int Help()
{
    Console.WriteLine("""
        adate image harness

          stats                                 report ComfyUI version and VRAM headroom
          generate <workflow> <positive> ...    run a workflow and store the result
          free                                  unload models and free VRAM

        options for generate:
          --negative <text>      --seed <n>            --size <WxH>
          --anchor <hash>        --anchor-weight <d>

        configuration (environment, ADATE_ prefix):
          ADATE_Comfy__BaseAddress=http://gpu-box:8188
          ADATE_Workflows__Directory=workflows
          ADATE_ImageStore__Directory=./out
        """);

    return 0;
}
