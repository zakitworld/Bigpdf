using Bigpdf.Components;
using Bigpdf.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
    })
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 1024 * 1024 * 64; // 64 MB
    });

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication("Cookie")
    .AddCookie("Cookie", options =>
    {
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton<IPdfService, PdfService>();
builder.Services.AddSingleton<IPdfProcessor, PdfProcessor>();
builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
builder.Services.AddSingleton<IJobStore, JobStore>();
builder.Services.AddSingleton<IJobService, JobService>();
builder.Services.AddSingleton<IAdminAuthSettings, AdminAuthSettings>();
builder.Services.AddHostedService<BackgroundWorker>();
builder.Services.AddHttpClient();
builder.Services.AddScoped(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    return factory.CreateClient();
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapPost("/auth/login", async (HttpContext ctx, IAdminAuthSettings authSettings) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var user = form["username"].ToString();
    var pass = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    if (string.Equals(user, authSettings.Username, StringComparison.Ordinal) && string.Equals(pass, authSettings.Password, StringComparison.Ordinal))
    {
        var claims = new[] { new Claim(ClaimTypes.Name, user) };
        var identity = new ClaimsIdentity(claims, "Cookie");
        var principal = new ClaimsPrincipal(identity);
        await ctx.SignInAsync("Cookie", principal);

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

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync("Cookie");
    ctx.Response.Redirect("/login");
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/api/jobs/start", async (HttpContext ctx, IJobService jobService) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<Bigpdf.Models.JobRequest>();
    if (req == null) return Results.BadRequest();
    var job = await jobService.EnqueueJobAsync(req);
    return Results.Json(job);
}).RequireAuthorization();

app.MapGet("/api/jobs/{id}", (string id, IJobService jobService) =>
{
    var job = jobService.GetJob(id);
    return job != null ? Results.Json(job) : Results.NotFound();
}).RequireAuthorization();

app.MapGet("/api/jobs", (IJobService jobService) => Results.Json(jobService.ListJobs())).RequireAuthorization();

app.MapGet("/uploads/{**filePath}", (string filePath, IWebHostEnvironment env) =>
{
    if (!UploadPaths.TryResolveUploadPath(env, filePath, out var fullPath))
        return Results.NotFound();

    if (Directory.Exists(fullPath))
        return Results.NotFound();

    if (!File.Exists(fullPath))
        return Results.NotFound();

    return Results.File(fullPath);
}).RequireAuthorization();

app.MapGet("/api/uploads/list", (string path, IWebHostEnvironment env) =>
{
    if (!UploadPaths.TryResolveUploadPath(env, path, out var fullPath) || !Directory.Exists(fullPath))
        return Results.NotFound();

    var uploadsRoot = UploadPaths.GetUploadsRoot(env);
    var files = Directory.EnumerateFiles(fullPath)
        .OrderBy(f => f)
        .Select(f => UploadPaths.ToPublicUrl(Path.Combine("uploads", Path.GetRelativePath(uploadsRoot, f))))
        .ToList();

    return Results.Json(files);
}).RequireAuthorization();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
