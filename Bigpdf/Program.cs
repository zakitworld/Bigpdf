using Bigpdf.Components;
using Bigpdf.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
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
builder.Services.AddSingleton<IFileValidationService, FileValidationService>();

// Background workers
builder.Services.AddHostedService<BackgroundWorker>();
builder.Services.AddHostedService<FileCleanupService>();

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("UploadPolicy", policy =>
    {
        policy.PermitLimit = 30;
        policy.Window = TimeSpan.FromMinutes(1);
        policy.QueueLimit = 5;
    });
});

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

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
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

// Chunked Upload Endpoints
var tempChunksStore = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

app.MapPost("/api/uploads/create", (HttpContext ctx) =>
{
    var uploadId = Guid.NewGuid().ToString("N");
    return Results.Ok(new { uploadId });
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/api/uploads/chunk/status", (string uploadId, IWebHostEnvironment env) =>
{
    var tempDir = Path.Combine(env.ContentRootPath, "uploads", "_temp", uploadId);
    if (!Directory.Exists(tempDir)) return Results.Ok(new { uploadedChunks = Array.Empty<int>() });

    var files = Directory.GetFiles(tempDir, "*.chunk");
    var uploadedChunks = files.Select(f =>
    {
        var name = Path.GetFileNameWithoutExtension(f);
        return int.TryParse(name, out var idx) ? idx : -1;
    }).Where(idx => idx >= 0).OrderBy(x => x).ToArray();

    return Results.Ok(new { uploadedChunks });
}).AllowAnonymous();

app.MapPost("/api/uploads/chunk", async (HttpContext ctx, IWebHostEnvironment env, IFileValidationService validator) =>
{
    var uploadId = ctx.Request.Headers["X-Upload-Id"].ToString();
    var fileName = ctx.Request.Headers["X-File-Name"].ToString();
    var chunkIndexStr = ctx.Request.Headers["X-Chunk-Index"].ToString();

    if (string.IsNullOrEmpty(uploadId) || !int.TryParse(chunkIndexStr, out var chunkIndex))
    {
        return Results.BadRequest("Invalid chunk upload headers");
    }

    var tempDir = Path.Combine(env.ContentRootPath, "uploads", "_temp", uploadId);
    Directory.CreateDirectory(tempDir);

    var chunkPath = Path.Combine(tempDir, $"{chunkIndex}.chunk");
    await using (var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        await ctx.Request.Body.CopyToAsync(fs);
    }

    return Results.Ok(new { success = true });
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/api/uploads/chunk/complete", async (string uploadId, string fileName, int totalChunks, IWebHostEnvironment env, IPdfService pdfService, IFileValidationService validator) =>
{
    if (string.IsNullOrEmpty(uploadId) || string.IsNullOrEmpty(fileName))
    {
        return Results.BadRequest(new { success = false, error = "Invalid parameters" });
    }

    var tempDir = Path.Combine(env.ContentRootPath, "uploads", "_temp", uploadId);
    if (!Directory.Exists(tempDir))
    {
        return Results.BadRequest(new { success = false, error = "Upload chunks not found" });
    }

    var safeName = Path.GetFileName(fileName);
    var finalPath = Path.Combine(UploadPaths.GetUploadsRoot(env), safeName);

    await using (var destStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        for (int i = 0; i < totalChunks; i++)
        {
            var chunkPath = Path.Combine(tempDir, $"{i}.chunk");
            if (!File.Exists(chunkPath))
            {
                return Results.BadRequest(new { success = false, error = $"Missing chunk {i}" });
            }

            await using (var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read))
            {
                await chunkStream.CopyToAsync(destStream);
            }
        }
    }

    // Validate magic bytes after assembling
    await using (var checkStream = new FileStream(finalPath, FileMode.Open, FileAccess.Read))
    {
        var isValid = await validator.ValidateFileMagicBytesAsync(checkStream, safeName);
        if (!isValid)
        {
            checkStream.Close();
            File.Delete(finalPath);
            try { Directory.Delete(tempDir, true); } catch { }
            return Results.BadRequest(new { success = false, error = "File validation failed: invalid format or signature" });
        }
    }

    try { Directory.Delete(tempDir, true); } catch { }

    var relativePath = Path.Combine("uploads", safeName).Replace('\\', '/');
    return Results.Ok(new { success = true, path = relativePath });
}).AllowAnonymous().DisableAntiforgery();

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