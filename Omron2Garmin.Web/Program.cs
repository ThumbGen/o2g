using Omron2Garmin.Web.Components;
using Omron2Garmin.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register application services
// Use memory cache to persist Garmin sessions across Blazor circuit reconnections
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GarminSessionStore>();
builder.Services.AddScoped<GarminService>();
builder.Services.AddSingleton<OmronCsvParser>();
builder.Services.AddScoped<TimezoneService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// Remove status code pages that might cause issues
app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
