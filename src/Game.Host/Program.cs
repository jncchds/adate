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
    o.LocationsFile = ResolveContentPath(o.LocationsFile);
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
    new JsonLocationCatalog(sp.GetRequiredService<IOptions<StudioOptions>>().Value.LocationsFile));

// One compiler per prompt dialect. Spike 0 ships the booru dialect only.
builder.Services.AddSingleton<IPromptCompiler, BooruPromptCompiler>();

builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<CharacterStudio>();

var app = builder.Build();

// Create or upgrade the schema before anything can query it.
app.Services.GetRequiredService<Database>().Migrate();

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
