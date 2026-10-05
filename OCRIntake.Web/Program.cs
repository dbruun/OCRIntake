using OCRIntake.Web;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 11 * 1024 * 1024);
builder.Services.AddSingleton<ComplianceService>();
builder.Services.AddSingleton<IntakeStore>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddHttpClient<ContentUnderstandingService>(client => client.Timeout = TimeSpan.FromSeconds(120))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Method != "GET" &&
            (context.Request.Headers["X-Intake-Request"] != "1" ||
             (context.Request.Headers.TryGetValue("Origin", out var origin) &&
              origin != $"{context.Request.Scheme}://{context.Request.Host}")))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    try { await next(context); }
    catch (BadHttpRequestException)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid request or upload too large." });
        }
    }
    catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError("Intake operation failed: {Type}", exception.GetType().Name);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 502;
            await context.Response.WriteAsJsonAsync(new { error = "The operation failed. Check server configuration or retry. Your intake has not been approved." });
        }
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/config", (IConfiguration config) => new
{
    mode = config["ContentUnderstanding:Mode"] ?? "Live",
    fields = LabelSchema.Fields,
    fieldsDeprecated = true
});
app.MapGet("/api/profiles", async (ProfileStore profiles) => Results.Ok(await profiles.List()));
app.MapGet("/api/profiles/{id:guid}/versions/{version:int}", async (Guid id, int version, ProfileStore profiles) =>
{
    var profile = await profiles.Get(id, version);
    return profile is null ? Results.NotFound() : Results.Ok(profile);
});
app.MapPost("/api/profiles", async (ProfileRequest request, ProfileStore profiles) =>
{
    var error = ProfileStore.Validate(request);
    if (error is not null) return Results.BadRequest(new { error });
    var profile = await profiles.Save(null, request);
    return profile is null
        ? Results.Conflict(new { error = "New profiles require version 0; the demo allows at most 50 profiles." })
        : Results.Ok(profile);
});
app.MapPost("/api/profiles/{id:guid}", async (Guid id, ProfileRequest request, ProfileStore profiles) =>
{
    var error = ProfileStore.Validate(request);
    if (error is not null) return Results.BadRequest(new { error });
    var profile = await profiles.Save(id, request);
    return profile is null
        ? Results.Conflict(new { error = "Profile changed or does not exist. Reload settings before saving." })
        : Results.Ok(profile);
});

app.MapPost("/api/intakes", async (HttpRequest request, ContentUnderstandingService extraction,
    ComplianceService compliance, IntakeStore store, ProfileStore profiles, CancellationToken cancellation) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Upload an image as multipart form data." });
    var form = await request.ReadFormAsync(cancellation);
    var profileId = Guid.Empty;
    int? profileVersion = null;
    if ((form.ContainsKey("profileId") && !Guid.TryParse(form["profileId"], out profileId)) ||
        (form.ContainsKey("profileVersion") &&
         (!int.TryParse(form["profileVersion"], out var parsedVersion) || (profileVersion = parsedVersion) < 1)))
        return Results.BadRequest(new { error = "Select a valid extraction profile and version." });
    var profile = await profiles.Get(profileId, profileVersion);
    if (profile is null)
        return Results.BadRequest(new { error = "The selected extraction profile version does not exist." });
    if (form.Files.Count != 1 || form.Files[0].Length is <= 0 or > 10 * 1024 * 1024)
        return Results.BadRequest(new { error = "Upload one JPEG or PNG image, at most 10 MB." });
    var file = form.Files[0];
    await using var stream = file.OpenReadStream();
    using var memory = new MemoryStream();
    await stream.CopyToAsync(memory, cancellation);
    var image = memory.ToArray();
    var png = image.Length >= 8 && image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    var jpeg = image.Length >= 3 && image[0] == 255 && image[1] == 216 && image[2] == 255;
    if (!png && !jpeg)
        return Results.BadRequest(new { error = "Only JPEG and PNG images are supported." });
    var mediaType = png ? "image/png" : "image/jpeg";
    var detected = await extraction.Extract(image, mediaType, profile, cancellation);
    var values = detected.ToDictionary(x => x.Key, x => x.Value.Value);
    var context = new ReviewContext("Other", "", false, false);
    var intake = new Intake(Guid.NewGuid(), Path.GetFileName(file.FileName), DateTimeOffset.UtcNow,
        extraction.Mode, detected, values, context, compliance.Screen(values, context),
        SourceSha256: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)), Profile: profile);
    if (!store.TryAdd(new(intake, image, mediaType)))
        return Results.Problem("Demo draft capacity reached. Retry after drafts expire (two hours).", statusCode: 503);
    return Results.Ok(intake);
});

app.MapGet("/api/intakes/{id:guid}", async (Guid id, IntakeStore store) =>
{
    var entry = store.Get(id);
    if (entry is null) return Results.NotFound();
    await entry.Gate.WaitAsync();
    try { return Results.Ok(entry.Intake); }
    finally { entry.Gate.Release(); }
});
app.MapGet("/api/intakes/{id:guid}/image", (Guid id, IntakeStore store) =>
{
    var entry = store.Get(id);
    return entry is null ? Results.NotFound() : Results.File(entry.Image, entry.MediaType);
});
app.MapGet("/api/approved/{id:guid}", async (Guid id, IntakeStore store) =>
{
    var intake = await store.ReadApproved(id);
    return intake is null ? Results.NotFound() : Results.Ok(intake);
});
app.MapPost("/api/intakes/{id:guid}/review", async (Guid id, ReviewRequest review,
    IntakeStore store, ComplianceService compliance) =>
{
    var entry = store.Get(id);
    if (entry is null) return Results.NotFound();
    await entry.Gate.WaitAsync();
    try
    {
        if (review.Values is null || review.Context is null ||
            review.Values.Count != entry.Intake.Detected.Count ||
            entry.Intake.Detected.Keys.Any(key => !review.Values.ContainsKey(key)) ||
            review.Values.Values.Any(value => value?.Length > 10000) ||
            review.Context.Category is not ("Food" or "Other") ||
            review.Context.State is null || review.Context.State.Length > 100)
            return Results.BadRequest(new { error = "Provide all known fields and a valid review context." });
        if (entry.Intake.ApprovedAt is not null || review.Revision != entry.Intake.Revision)
            return Results.Conflict(new { error = "This intake was approved or changed. Reload it before reviewing." });
        entry.Intake = entry.Intake with
        {
            Values = review.Values, Context = review.Context,
            Screening = compliance.Screen(review.Values, review.Context), Revision = entry.Intake.Revision + 1
        };
        return Results.Ok(entry.Intake);
    }
    finally { entry.Gate.Release(); }
});
app.MapPost("/api/intakes/{id:guid}/approve", async (Guid id, ApprovalRequest approval, IntakeStore store) =>
{
    var entry = store.Get(id);
    if (entry is null) return Results.NotFound();
    if (!approval.Approved || string.IsNullOrWhiteSpace(approval.Inspector) || approval.Inspector.Length > 200)
        return Results.BadRequest(new { error = "Explicit approval and an inspector name are required." });
    await entry.Gate.WaitAsync();
    try
    {
        if (entry.Intake.ApprovedAt is not null || entry.Intake.Revision == 0 || approval.Revision != entry.Intake.Revision)
            return Results.Conflict(new { error = "Save the review first. Approved or stale revisions cannot be approved." });
        var approved = entry.Intake with
        {
            Status = "Approved", Inspector = approval.Inspector.Trim(), ApprovedAt = DateTimeOffset.UtcNow
        };
        await store.SaveApproved(approved);
        entry.Intake = approved;
        return Results.Ok(approved);
    }
    finally { entry.Gate.Release(); }
});
app.Run();
