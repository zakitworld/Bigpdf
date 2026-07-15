using Bigpdf.Components;
using Bigpdf.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.IO;

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
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// Serve static files (js/upload.js, etc.) early so they're available before route matching
app.UseStaticFiles();
app.MapStaticAssets();

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

// Create upload id endpoint - server-issued upload IDs for resumable uploads
app.MapPost("/api/uploads/create", async (HttpContext ctx, IWebHostEnvironment env) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var uploadsRoot = UploadPaths.GetUploadsRoot(env);
    Directory.CreateDirectory(uploadsRoot);

    var uploadId = Guid.NewGuid().ToString("N");
    var uploadDir = Path.Combine(uploadsRoot, uploadId);
    Directory.CreateDirectory(uploadDir);

    return Results.Json(new { uploadId });
}).RequireAuthorization();

// Chunked upload endpoint: accepts raw chunk bodies with headers: X-Upload-Id, X-File-Name, X-Chunk-Index, X-Total-Chunks
app.MapPost("/api/uploads/chunk", async (HttpContext ctx, IWebHostEnvironment env) =>
{
    var req = ctx.Request;
    var headers = req.Headers;

    if (!headers.TryGetValue("X-File-Name", out var fileNameVals))
        return Results.BadRequest();

    var fileName = Path.GetFileName(fileNameVals.ToString() ?? "upload");
    var uploadId = headers.TryGetValue("X-Upload-Id", out var idVal) && !string.IsNullOrWhiteSpace(idVal.ToString())
        ? idVal.ToString()!
        : Guid.NewGuid().ToString("N");

    var chunkIndexHeader = headers.TryGetValue("X-Chunk-Index", out var chunkIndexVals) ? chunkIndexVals.ToString() : string.Empty;
    var totalChunksHeader = headers.TryGetValue("X-Total-Chunks", out var totalChunksVals) ? totalChunksVals.ToString() : string.Empty;

    if (!int.TryParse(chunkIndexHeader, out var chunkIndex)) chunkIndex = 0;
    if (!int.TryParse(totalChunksHeader, out var totalChunks)) totalChunks = 1;

    var uploadsRoot = UploadPaths.GetUploadsRoot(env);
    var uploadDir = Path.Combine(uploadsRoot, uploadId);
    Directory.CreateDirectory(uploadDir);

    var chunkName = $"chunk_{chunkIndex}.part";
    var chunkPath = Path.Combine(uploadDir, chunkName);

    try
    {
        // Write chunk to its own file (supports parallel and resumable uploads)
        await using (var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await req.Body.CopyToAsync(fs);
        }

        return Results.Json(new { status = "ok" });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

// Status endpoint: returns list of uploaded chunk indexes for an uploadId
app.MapGet("/api/uploads/chunk/status", (string uploadId, IWebHostEnvironment env) =>
{
    if (string.IsNullOrWhiteSpace(uploadId)) return Results.BadRequest();
    var uploadsRoot = UploadPaths.GetUploadsRoot(env);
    var uploadDir = Path.Combine(uploadsRoot, uploadId);
    if (!Directory.Exists(uploadDir)) return Results.Json(new { uploadedChunks = Array.Empty<int>() });

    var files = Directory.EnumerateFiles(uploadDir)
        .Select(Path.GetFileName)
        .Where(n => !string.IsNullOrWhiteSpace(n) && n.StartsWith("chunk_"))
        .Select(n => n![6..].Replace(".part", ""))
        .Select(s => int.TryParse(s, out var i) ? i : -1)
        .Where(i => i >= 0)
        .OrderBy(i => i)
        .ToArray();

    return Results.Json(new { uploadedChunks = files });
});

// Complete endpoint: assembles chunks into final file and returns public path
app.MapPost("/api/uploads/chunk/complete", async (HttpContext ctx, IWebHostEnvironment env) =>
{
    var q = ctx.Request.Query;
    var uploadId = q["uploadId"].ToString();
    var fileName = Path.GetFileName(q["fileName"].ToString() ?? "upload");
    if (!int.TryParse(q["totalChunks"], out var totalChunks)) totalChunks = 1;

    if (string.IsNullOrWhiteSpace(uploadId) || string.IsNullOrWhiteSpace(fileName))
        return Results.BadRequest();

    var uploadsRoot = UploadPaths.GetUploadsRoot(env);
    var uploadDir = Path.Combine(uploadsRoot, uploadId);
    if (!Directory.Exists(uploadDir)) return Results.Problem("Upload not found");

    try
    {
        var finalName = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{fileName}";
        var finalPath = Path.Combine(uploadsRoot, finalName);

        await using (var finalFs = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (int i = 0; i < totalChunks; i++)
            {
                var chunkPath = Path.Combine(uploadDir, $"chunk_{i}.part");
                if (!File.Exists(chunkPath)) throw new FileNotFoundException($"Missing chunk {i}");

                await using (var cs = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await cs.CopyToAsync(finalFs);
                }
            }
        }

        // delete chunk files and directory
        foreach (var f in Directory.EnumerateFiles(uploadDir)) File.Delete(f);
        Directory.Delete(uploadDir);

        var publicPath = Path.Combine("uploads", finalName).Replace('\\', '/');
        return Results.Json(new { path = $"/uploads/{finalName}" });
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
});

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
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
