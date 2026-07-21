using Bigpdf.Components;
using Bigpdf.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Reflection;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// ===== Configuration & Services =====
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

builder.Services.AddCascadingAuthenticationState();

// Improved Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization();

// Register core services
builder.Services.AddSingleton<IPdfService, PdfService>();
builder.Services.AddSingleton<IPdfProcessor, PdfProcessor>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddSingleton<IAdminAuthSettings, AdminAuthSettings>();

// Background worker for job processing
builder.Services.AddHostedService<BackgroundWorker>();

// HTTP Client
builder.Services.AddHttpClient();

// Logging
builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

var app = builder.Build();

// ===== Middleware Pipeline =====
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

// ===== Custom Endpoints =====

// Login endpoint
app.MapPost("/auth/login", async (HttpContext ctx, IAdminAuthSettings authSettings) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var user = form["username"].ToString() ?? "";
    var pass = form["password"].ToString() ?? "";
    var returnUrl = form["returnUrl"].ToString() ?? "";

    if (string.Equals(user, authSettings.Username, StringComparison.Ordinal) &&
        string.Equals(pass, authSettings.Password, StringComparison.Ordinal))
    {
        var claims = new[] { new Claim(ClaimTypes.Name, user) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        var redirect = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/')
            ? "/"
            : returnUrl;

        ctx.Response.Redirect(redirect);
        return;
    }

    var fallbackUrl = string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/')
        ? "/login"
        : $"/login?returnUrl={Uri.EscapeDataString(returnUrl)}";

    ctx.Response.Redirect($"{fallbackUrl}&error=invalid");
}).AllowAnonymous().DisableAntiforgery();

// Logout endpoint
app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/login");
}).RequireAuthorization().DisableAntiforgery();

// Job endpoints (existing + improved)
app.MapPost("/api/jobs/start", async (HttpContext ctx, IJobService jobService) =>
{
    try
    {
        var req = await ctx.Request.ReadFromJsonAsync<Bigpdf.Models.JobRequest>();
        if (req == null) return Results.BadRequest("Invalid request");

        var job = await jobService.EnqueueJobAsync(req);
        return Results.Json(job);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

app.MapGet("/api/jobs/{id}", (string id, IJobService jobService) =>
{
    var job = jobService.GetJob(id);
    return job != null ? Results.Json(job) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/jobs", (IJobService jobService) =>
    Results.Json(jobService.ListJobs())
).RequireAuthorization();

// Keep your existing chunked upload endpoints here...

// Static file serving for uploads
app.MapGet("/uploads/{**filePath}", (string filePath, IWebHostEnvironment env) =>
{
    if (!UploadPaths.TryResolveUploadPath(env, filePath, out var fullPath))
        return Results.NotFound();

    if (!File.Exists(fullPath))
        return Results.NotFound();

    return Results.File(fullPath);
});

// Final mapping
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();