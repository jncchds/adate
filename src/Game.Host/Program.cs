using Game.Data;
using Game.Host.Components;
using Game.Host.Services;
using Game.Imaging.Caching;
using Game.Play;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Every model endpoint is configuration. The game, the image service and the LLM are
// independently addressable and are not assumed to share a machine.
builder.Configuration.AddEnvironmentVariables("ADATE_");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Style packs, locations and workflow graphs ship alongside the binary, so their paths resolve
// against it. Writable state -- the database and the image store -- stays relative to the working
// directory, because that is state the player owns rather than content we ship.
builder.Services.AddGame(builder.Configuration, AppContext.BaseDirectory);

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
