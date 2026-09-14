using Game.Core;
using Game.Core.Content;
using Game.Core.Style;
using Game.Data;
using Game.Data.Repositories;
using Game.Host.Components;
using Game.Host.Services;
using Game.Imaging;
using Game.Imaging.Caching;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Every model endpoint is configuration. The game, the image service and the LLM are
// independently addressable and are not assumed to share a machine.
builder.Configuration.AddEnvironmentVariables("ADATE_");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddImaging(builder.Configuration);
builder.Services.AddData(builder.Configuration);
Game.Llm.LlmServiceCollectionExtensions.AddLlm(builder.Services, builder.Configuration);

builder.Services.Configure<StudioOptions>(builder.Configuration.GetSection(StudioOptions.SectionName));

// Style packs, locations and workflow graphs ship alongside the binary, so their paths
// resolve against it rather than against the working directory. Otherwise `dotnet run`
// (cwd = project directory) and a published build (cwd = wherever it was launched from)
// look in two different places for the same file.
//
// Writable state -- the database and the image store -- deliberately stays relative to the
// working directory, because that is state the player owns rather than content we ship.
builder.Services.PostConfigure<StudioOptions>(o =>
{
    o.StylePackDirectory = ResolveContentPath(o.StylePackDirectory);
    o.PlaceTypesFile = ResolveContentPath(o.PlaceTypesFile);
    o.SettingsDirectory = ResolveContentPath(o.SettingsDirectory);
    o.EncountersDirectory = ResolveContentPath(o.EncountersDirectory);
    o.PoseDirectory = ResolveContentPath(o.PoseDirectory);
    o.TemperFile = ResolveContentPath(o.TemperFile);
    o.WantsFile = ResolveContentPath(o.WantsFile);
    o.ContrastsFile = ResolveContentPath(o.ContrastsFile);
    o.ValuesFile = ResolveContentPath(o.ValuesFile);
    o.PredicatesFile = ResolveContentPath(o.PredicatesFile);
    o.RelationshipFile = ResolveContentPath(o.RelationshipFile);
    o.RoutesFile = ResolveContentPath(o.RoutesFile);
    o.EndingsFile = ResolveContentPath(o.EndingsFile);
    o.WeatherFile = ResolveContentPath(o.WeatherFile);

    // Fail at startup rather than at the first render: a game that has declared an
    // impossible age floor should not serve a single page.
    o.Content.Validate();
});

builder.Services.PostConfigure<Game.Imaging.Workflows.WorkflowOptions>(o =>
    o.Directory = ResolveContentPath(o.Directory));

static string ResolveContentPath(string path) =>
    Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

// Content: style packs and locations are data, not code (HANDOFF 1.5).
builder.Services.AddSingleton(sp =>
    new JsonStylePackLoader(sp.GetRequiredService<IOptions<StudioOptions>>().Value.StylePackDirectory));
builder.Services.AddSingleton<IStylePackLoader>(sp => sp.GetRequiredService<JsonStylePackLoader>());

builder.Services.AddSingleton<ILocationCatalog>(sp =>
    new JsonLocationCatalog(sp.GetRequiredService<IOptions<StudioOptions>>().Value.PlaceTypesFile));

builder.Services.AddSingleton<Game.Core.Settings.ISettingCatalog>(sp => new Game.Core.Settings.JsonSettingCatalog(
    sp.GetRequiredService<IOptions<StudioOptions>>().Value.SettingsDirectory,
    sp.GetRequiredService<ILocationCatalog>()));

builder.Services.AddSingleton<Game.Core.Encounters.IEncounterCatalog>(sp => new Game.Core.Encounters.JsonEncounterCatalog(
    sp.GetRequiredService<IOptions<StudioOptions>>().Value.EncountersDirectory,
    sp.GetRequiredService<Game.Core.Settings.ISettingCatalog>(),
    [.. sp.GetRequiredService<Game.Core.Cast.RouteContent>().Routes.Select(r => r.Id)]));

builder.Services.AddSingleton<WorldService>();

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    return Game.Core.Cast.CastContent.Load(o.TemperFile, o.WantsFile, o.ContrastsFile);
});

builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<StudioOptions>>().Value;
    var story = Game.Core.Story.StoryContent.Load(o.ValuesFile, o.PredicatesFile, o.RelationshipFile);
    story.ValidateAgainst(sp.GetRequiredService<Game.Core.Cast.CastContent>());
    return story;
});

builder.Services.AddSingleton(sp =>
{
    var routes = Game.Core.Cast.RouteContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.RoutesFile);
    routes.ValidateAgainst(sp.GetRequiredService<Game.Core.Cast.CastContent>());
    return routes;
});

builder.Services.AddSingleton(sp =>
    Game.Core.Story.EndingContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.EndingsFile));

builder.Services.AddSingleton(sp =>
{
    var weather = Game.Core.World.WeatherContent.Load(sp.GetRequiredService<IOptions<StudioOptions>>().Value.WeatherFile);
    weather.ValidateAgainst(sp.GetRequiredService<ILocationCatalog>(), sp.GetRequiredService<Game.Core.Settings.ISettingCatalog>());
    return weather;
});

// One compiler per prompt dialect; the pack's dialect picks which one runs.
builder.Services.AddSingleton<IPromptCompiler, BooruPromptCompiler>();
builder.Services.AddSingleton<IPromptCompiler, NaturalPromptCompiler>();
builder.Services.AddSingleton<PromptCompilers>();

// Pose skeletons are shipped content, seeded into the image store on first use.
builder.Services.AddSingleton(sp => new PoseCatalog(
    sp.GetRequiredService<IImageStore>(),
    sp.GetRequiredService<IOptions<StudioOptions>>().Value.PoseDirectory));

builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<CharacterStudio>();

var app = builder.Build();

// Create or upgrade the schema before anything can query it.
app.Services.GetRequiredService<Database>().Migrate();

// `dotnet run --project src/Game.Host -- measure <run|judge|retrieval>` runs the LLM measures with
// the game's own services instead of serving pages (phase-2 plan build step 9).
if (args is ["measure", ..])
{
    return await MeasureRunner.RunAsync(app.Services, args[1..]);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();

// Generated images are content-addressed: a filename encodes every parameter that produced
// it, so a given name can only ever hold one image (HANDOFF 1.7). That makes them safe to
// serve as immutable, which turns the browser cache into a second tier of the image cache
// and makes an expression change a zero-request crossfade.
var imageOptions = app.Services.GetRequiredService<IOptions<ImageStoreOptions>>().Value;
var imageRoot = Path.GetFullPath(imageOptions.Directory);
Directory.CreateDirectory(imageRoot);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imageRoot),
    RequestPath = $"/{imageOptions.RelativePrefix.Trim('/')}",
    ContentTypeProvider = new FileExtensionContentTypeProvider(),
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable",
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
