using Mattiverse.Core;
using Mattiverse.Core.Backups;
using Mattiverse.Core.Configuration;
using Mattiverse.Core.Data;
using Mattiverse.Core.Entities;
using Mattiverse.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

// "docker HEALTHCHECK": the image has no curl, so the app checks itself and exits with 0 (healthy) or 1.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration / options
// ---------------------------------------------------------------------------
builder.Services.Configure<MattiverseOptions>(builder.Configuration.GetSection(MattiverseOptions.SectionName));

// Resolve the data directory before the container is built (needed for keys + SQLite path).
// In Docker the Dockerfile sets Mattiverse__DataPath=/data; local runs fall back to App_Data.
var configuredDataPath = builder.Configuration[$"{MattiverseOptions.SectionName}:DataPath"];
var dataPath = !string.IsNullOrWhiteSpace(configuredDataPath)
    ? configuredDataPath
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data");

var appPaths = new AppPaths(dataPath);
appPaths.EnsureCreated();
builder.Services.AddSingleton(appPaths);
builder.Services.PostConfigure<MattiverseOptions>(options => options.DataPath = dataPath);

var sessionLifetimeDays = builder.Configuration.GetValue($"{MattiverseOptions.SectionName}:SessionLifetimeDays", 30);

// ---------------------------------------------------------------------------
// Infrastructure services
// ---------------------------------------------------------------------------

// Data Protection keys live on the mounted volume so cookies and encrypted secrets survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("Mattiverse")
    .PersistKeysToFileSystem(new DirectoryInfo(appPaths.KeysPath));

builder.Services.AddDbContext<MattiverseDbContext>(options => options.UseSqlite(appPaths.SqliteConnectionString));

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentUserAccessor, HttpCurrentUserAccessor>();
builder.Services.AddSingleton<SetupState>();

// Honour X-Forwarded-* from a TLS-terminating reverse proxy so Request.IsHttps is correct and the
// auth cookie is marked Secure when the client connection is HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddMattiverseCore();
builder.Services.AddMattiverseWorkers();
builder.Services.AddScoped<Mattiverse.Web.Support.AppUrls>();
builder.Services.AddScoped<Mattiverse.Web.Support.FrameProbe>();
builder.Services.AddHostedService<DockerStartupService>();

// ---------------------------------------------------------------------------
// Authentication / authorization
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, options =>
    {
        options.Cookie.Name = "mattiverse.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, sessionLifetimeDays));
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = SessionValidation.ValidateAsync;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(nameof(UserRole.Admin)));
    // Backstop: any endpoint without an explicit policy still requires an authenticated user.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// ---------------------------------------------------------------------------
// Razor Pages (secure by default; open only what is needed before login)
// ---------------------------------------------------------------------------
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Logout");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Share/View");

    // Roles: Admin manages everything; User opens apps and looks at them; Anonymous only sees a shared link.
    foreach (var folder in new[] { "/Store", "/Backups", "/Jobs", "/Users", "/Settings", "/Wizard" })
    {
        options.Conventions.AuthorizeFolder(folder, "AdminOnly");
    }

    foreach (var page in new[] { "/Apps/Update", "/Apps/Uninstall", "/Apps/Share" })
    {
        options.Conventions.AuthorizePage(page, "AdminOnly");
    }
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// HTTP pipeline
// ---------------------------------------------------------------------------
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

var staticContentTypes = new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });
// A 404 or 403 shows a page of the app (with the original status code) instead of an empty response.
app.UseStatusCodePagesWithReExecute("/StatusCode/{0}");
app.UseRouting();
app.UseAuthentication();
// First run: redirect everything to /Setup until an administrator exists.
app.UseMiddleware<SetupGuardMiddleware>();
app.UseMiddleware<PasswordChangeGuardMiddleware>();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet("/healthz", () => Results.Text("ok " + AppVersion.Current)).AllowAnonymous();
app.MapJobsApi();

// ---------------------------------------------------------------------------
// Database migration + first-run seed
// ---------------------------------------------------------------------------
await DbBootstrapper.InitializeAsync(app.Services, app.Logger);

app.Run();
return 0;
