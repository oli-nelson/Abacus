using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Abacus.Dashboard;

internal sealed class DashboardHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly DashboardStream stream;
    private readonly IssueActions? actions;
    private readonly DashboardRuntime? runtime;
    private readonly GitCollector? git;
    private readonly object disposalGate = new();
    private Task? disposal;
    private DashboardHost(WebApplication app, DashboardStream stream, IssueActions? actions, DashboardRuntime? runtime, GitCollector? git)
    { this.app = app; this.stream = stream; this.actions = actions; this.runtime = runtime; this.git = git; }
    public CancellationToken Stopped => app.Lifetime.ApplicationStopped;

    public static async Task<DashboardHost> StartAsync(DashboardBinding binding, DashboardStream stream,
        string projectName, string actor, CancellationToken token, GitCollector? git = null, IssueActions? actions = null, IssueActivity? activity = null, bool integrated = false, DashboardRuntime? runtime = null, IssueCollector? issueCollector = null)
    {
        // Empty builder: never read arbitrary cwd appsettings, environment endpoint
        // overrides, development secrets or hosting startup assemblies.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = typeof(DashboardHost).Assembly.FullName });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 64 * 1024;
            options.Limits.MaxConcurrentConnections = 128;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in binding.Addresses) options.Listen(address, binding.Port);
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        var app = builder.Build();
        var project = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            runtimeSession = runtime?.Instance,
            name = projectName, actor, mode = integrated ? "integrated" : "standalone", runtimeConnected = runtime is not null,
            runtimeExplanation = runtime is not null ? "Live worker state from this run; claim Pause/Resume available when connected" : integrated ? "Hosted by this run; runtime state and controls are not yet available" : "Orchestrator not connected", unauthenticated = true,
            capabilities = new { readIssues = true, editIssues = actions is not null, createDrafts = actions?.DraftsEnabled ?? false, attention = actions?.AttentionEnabled ?? false, history = activity is not null, projectHistory = activity?.ProjectHistoryEnabled ?? false, git = git is not null, runtimeControl = runtime?.CanControlClaims ?? false, claimControl = runtime?.CanControlClaims ?? false },
        }, DashboardStream.Json);
        var assets = LoadAssets();
        var issueBodies = new DetailCache<string, byte[]>(256, CancellationToken.None);
        var historyBodies = new DetailCache<string, byte[]>(64, CancellationToken.None);
        var comparisonBodies = new DetailCache<string, byte[]>(32, CancellationToken.None);
        app.Run(async context =>
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.CacheControl = "no-cache";
            if (!binding.Allows(request.Host.Host, request.Host.Port)) { response.StatusCode = 400; return; }
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                if (request.ContentLength > 64 * 1024)
                {
                    // The rejected body is not consumed. Tell HTTP/1 clients not
                    // to reuse this connection before Kestrel closes its transport.
                    if (request.Protocol == "HTTP/1.1") response.Headers.Connection = "close";
                    response.StatusCode = 413; return;
                }
                if (!request.HasJsonContentType() || request.Headers["X-Abacus-Request"] != "1") { response.StatusCode = 400; return; }
                if (request.Headers.TryGetValue("Origin", out var origin) &&
                    (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) ||
                     uri.Scheme != request.Scheme || !string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                { response.StatusCode = 403; return; }
                var actionPath = request.Path.Value ?? "";
                if (request.Method == "POST" && actionPath == "/api/v1/run/actions")
                {
                    if (runtime is null) { response.StatusCode = 503; return; }
                    try
                    {
                        using var body = await ReadActionBodyAsync(request, context.RequestAborted);
                        var result = runtime.RunActions.Submit(runtime.Instance, DashboardRunActions.Parse(body.RootElement));
                        // Shutdown must never depend on a slow/disconnected browser.
                        if (result.Outcome == "accepted") runtime.RunActions.DispatchAccepted();
                        response.StatusCode = result.StatusCode;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                    }
                    catch (BadHttpRequestException ex) { response.StatusCode = ex.StatusCode; }
                    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { response.StatusCode = 400; }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    return;
                }
                if (request.Method == "POST" && actionPath is "/api/v1/runtime/workers/actions" or "/api/v1/runtime/supervisors/actions")
                {
                    if (runtime is null) { response.StatusCode = 503; return; }
                    try
                    {
                        using var body = await ReadActionBodyAsync(request, context.RequestAborted);
                        var controller = actionPath == "/api/v1/runtime/supervisors/actions" ? runtime.SupervisorActions : runtime.WorkerActions;
                        var result = controller.Submit(runtime.Instance, DashboardWorkerActions.Parse(body.RootElement));
                        response.StatusCode = result.StatusCode;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                    }
                    catch (BadHttpRequestException ex) { response.StatusCode = ex.StatusCode; }
                    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { response.StatusCode = 400; }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    return;
                }
                if (request.Method == "POST" && actionPath == "/api/v1/runtime/actions")
                {
                    if (runtime is null) { response.StatusCode = 503; return; }
                    try
                    {
                        using var body = await ReadActionBodyAsync(request, context.RequestAborted);
                        var result = runtime.Submit(DashboardRuntime.ParseControl(body.RootElement));
                        response.StatusCode = result.StatusCode;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                    }
                    catch (BadHttpRequestException ex) { response.StatusCode = ex.StatusCode; }
                    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { response.StatusCode = 400; }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    return;
                }
                if (request.Method == "POST" && actions is not null && actionPath == "/api/v1/issues/drafts")
                {
                    try
                    {
                        using var body = await ReadActionBodyAsync(request, context.RequestAborted);
                        var result = await actions.SubmitDraftAsync(IssueActions.ParseDraft(body.RootElement), context.RequestAborted);
                        response.StatusCode = result.StatusCode;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                    }
                    catch (BadHttpRequestException ex) { response.StatusCode = ex.StatusCode; }
                    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                    {
                        response.StatusCode = 400;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { outcome = "rejected", message = "Invalid draft JSON or fields; no write attempted." }, DashboardStream.Json));
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    return;
                }
                if (request.Method == "POST" && actions is not null && actionPath.StartsWith("/api/v1/issues/", StringComparison.Ordinal) && actionPath.EndsWith("/actions", StringComparison.Ordinal))
                {
                    if (actionPath.Length <= 23) { response.StatusCode = 404; return; }
                    var id = actionPath[15..^8];
                    if (id.Contains('/')) { response.StatusCode = 404; return; }
                    try
                    {
                        using var body = await ReadActionBodyAsync(request, context.RequestAborted);
                        var action = IssueActions.Parse(body.RootElement);
                        var result = await actions.SubmitAsync(id, action, context.RequestAborted);
                        response.StatusCode = result.StatusCode;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                    }
                    catch (BadHttpRequestException ex) { response.StatusCode = ex.StatusCode; }
                    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
                    {
                        response.StatusCode = 400;
                        await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { outcome = "rejected", message = "Invalid action JSON or fields; no write attempted." }, DashboardStream.Json));
                    }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                    return;
                }
                response.StatusCode = request.Path.StartsWithSegments("/api/v1") ? 503 : 404;
                return;
            }
            try
            {
                var path = request.Path.Value ?? "/";
                if (assets.TryGetValue(path, out var asset))
                {
                    response.ContentType = asset.Type;
                    if (request.Method != "HEAD") await response.Body.WriteAsync(asset.Bytes, context.RequestAborted);
                }
                else if (path.StartsWith("/api/v1/issues/", StringComparison.Ordinal) && path.EndsWith("/publication-review", StringComparison.Ordinal))
                {
                    if (actions is null || !actions.DraftsEnabled) { response.StatusCode = 503; return; }
                    if (request.Query.Count != 0 || path.Length <= 34) { response.StatusCode = 400; return; }
                    var id = path[15..^19];
                    try { await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await actions.ReviewPublicationAsync(id, context.RequestAborted), DashboardStream.Json)); }
                    catch (KeyNotFoundException) { response.StatusCode = 404; }
                    catch (ArgumentException) { response.StatusCode = 400; }
                    catch (Exception ex) when (ex is TargetException or ReasoningPolicyException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or CommandStartException or CommandTimeoutException or CommandOutputLimitException)
                    { response.StatusCode = 503; }
                }
                else if (path == "/api/v1/issues/drafts/context")
                {
                    if (actions is null || !actions.DraftsEnabled) { response.StatusCode = 503; return; }
                    try { await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(await actions.DraftContextAsync(context.RequestAborted), DashboardStream.Json)); }
                    catch (Exception ex) when (ex is TargetException or ReasoningPolicyException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                    { response.StatusCode = 503; }
                }
                else if (path == "/api/v1/mutations/context")
                {
                    if (actions is null) response.StatusCode = 503;
                    else await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { actions.Session, actions.ServerUnixMilliseconds, retryMinutes = IssueActions.RetryMinutes }, DashboardStream.Json));
                }
                else if (path == "/api/v1/runtime")
                {
                    if (runtime is null) { response.StatusCode = 503; return; }
                    var publication = runtime.State;
                    response.Headers.ETag = $"\"runtime-{runtime.Instance}-{publication.View.Revision}\"";
                    if (request.Headers.IfNoneMatch == response.Headers.ETag) response.StatusCode = 304;
                    else await JsonAsync(context, publication.Body);
                }
                else if (path == "/api/v1/project") await JsonAsync(context, project);
                else if (path == "/api/v1/diagnostics")
                {
                    await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Issues = issueCollector?.Metrics, Git = git?.Metrics,
                        Stream = new { stream.SerializedIssues, stream.SnapshotBuilds },
                        Scope = "Cumulative process-local measurements; stage durations are elapsed time, not isolated CPU. Reading diagnostics starts no source work."
                    }, DashboardStream.Json));
                }
                else if (path == "/api/v1/snapshot")
                {
                    var snapshot = stream.Snapshot();
                    response.Headers.ETag = snapshot.ETag;
                    if (request.Headers.IfNoneMatch == snapshot.ETag) response.StatusCode = 304;
                    else await JsonAsync(context, snapshot.Body);
                }
                else if (path == "/api/v1/branches")
                {
                    if (git is null) response.StatusCode = 503;
                    else
                    {
                        var publication = git.State;
                        response.Headers.ETag = $"\"git-{publication.View.Revision}\"";
                        if (request.Headers.IfNoneMatch == response.Headers.ETag) response.StatusCode = 304;
                        else await JsonAsync(context, publication.Body);
                    }
                }
                else if (path == "/api/v1/worktrees/events")
                {
                    if (request.Method != "GET") { response.StatusCode = 405; return; }
                    if (git is null) { response.StatusCode = 503; return; }
                    if (request.Query.Count != 1 || !request.Query.TryGetValue("id", out var id) || id.Count != 1 ||
                        id[0] is not { Length: 64 } key || key.Any(c => !Uri.IsHexDigit(c)))
                    { response.StatusCode = 400; return; }
                    using var watch = git.WatchWorktree(key);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
                    await WorktreeEventsAsync(context, watch, linked.Token);
                }
                else if (path == "/api/v1/worktrees/diff")
                {
                    if (git is null) { response.StatusCode = 503; return; }
                    if (request.Query.Count != 1 || !request.Query.TryGetValue("id", out var id) || id.Count != 1 ||
                        id[0] is not { Length: 64 } key || key.Any(c => !Uri.IsHexDigit(c)))
                    { response.StatusCode = 400; return; }
                    var result = await git.WorktreeDiffAsync(key, context.RequestAborted);
                    response.Headers.ETag = $"\"worktree-{result.Revision}\"";
                    if (request.Headers.IfNoneMatch == response.Headers.ETag) response.StatusCode = 304;
                    else await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                }
                else if (path == "/api/v1/branches/history")
                {
                    if (git is null) { response.StatusCode = 503; return; }
                    var limit = 100;
                    if (request.Query.Any(q => q.Value.Count != 1 || q.Key is not ("branch" or "limit")) ||
                        !request.Query.ContainsKey("branch") ||
                        (request.Query.ContainsKey("limit") && !int.TryParse(request.Query["limit"], out limit)))
                    { response.StatusCode = 400; return; }
                    var result = await git.HistoryAsync(request.Query["branch"].ToString(), limit, context.RequestAborted);
                    response.Headers.ETag = $"\"git-history-{result.Tip}-{result.HistoryBoundary}-{limit}\"";
                    if (request.Headers.IfNoneMatch == response.Headers.ETag) response.StatusCode = 304;
                    else await JsonAsync(context, await HistoryBodyAsync(historyBodies, result, limit, context.RequestAborted));
                }
                else if (path == "/api/v1/branches/compare")
                {
                    if (git is null) response.StatusCode = 503;
                    else
                    {
                        if (request.Query.Any(q => q.Value.Count != 1 || q.Key is not ("target" or "branch" or "patch")) ||
                            !request.Query.ContainsKey("target") || !request.Query.ContainsKey("branch") ||
                            (request.Query.ContainsKey("patch") && request.Query["patch"] != "true" && request.Query["patch"] != "false"))
                        { response.StatusCode = 400; return; }
                        var result = await git.CompareAsync(request.Query["target"].ToString(), request.Query["branch"].ToString(), request.Query["patch"] == "true", context.RequestAborted);
                        await JsonAsync(context, await ComparisonBodyAsync(comparisonBodies, result.Comparison, result.Patch, context.RequestAborted));
                    }
                }
                else if (path == "/api/v1/events" && request.Method == "GET") await EventsAsync(context, stream);
                else if (path.StartsWith("/api/v1/issues/", StringComparison.Ordinal) && path.EndsWith("/git", StringComparison.Ordinal))
                {
                    var id = path[15..^4];
                    if (id.Length == 0 || id.Contains('/')) { response.StatusCode = 404; return; }
                    if (request.Query.Any(q => q.Value.Count != 1 || q.Key is not ("patch" or "history") ||
                        q.Value[0] is not ("true" or "false"))) { response.StatusCode = 400; return; }
                    var issue = stream.Issue(id);
                    if (issue is null) { response.StatusCode = 404; return; }
                    if (git is null || stream.IssuesStale) { response.StatusCode = 503; return; }
                    var result = await git.IssueAsync(issue, context.RequestAborted, request.Query["patch"] == "true", request.Query["history"] == "true");
                    if (stream.IssuesStale || stream.Issue(id)?.Revision != issue.Revision) { response.StatusCode = 409; return; }
                    await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                }
                else if (path == "/api/v1/issues/activity")
                {
                    // Whole-project recorded history in one read. Without this the timeline
                    // pages issues in batches and only ever shows the batch it has loaded.
                    if (activity is null || !activity.ProjectHistoryEnabled) { response.StatusCode = 503; return; }
                    if (request.Query.Count != 0) { response.StatusCode = 400; return; }
                    var issues = stream.Issues;
                    if (issues is null || stream.IssuesStale) { response.StatusCode = 503; return; }
                    var loaded = await activity.ReadProjectAsync(context.RequestAborted);
                    // Every issue is stamped with the revision from this one snapshot, so a
                    // client that has already moved on simply re-reads; nothing is invented.
                    var page = new ProjectActivityPage(loaded.HistoryRevision, loaded.Coverage,
                        issues.Values.OrderBy(x => x.Id, StringComparer.Ordinal)
                            .Select(x => new ProjectActivityIssue(x.Id, x.Revision,
                                loaded.Issues.GetValueOrDefault(x.Id, [])))
                            .ToImmutableArray());
                    response.Headers.ETag = $"\"project-activity-{loaded.HistoryRevision}-{stream.Snapshot().ETag.Trim('"')}\"";
                    if (request.Headers.IfNoneMatch == response.Headers.ETag) { response.StatusCode = 304; return; }
                    await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, DashboardStream.Json));
                }
                else if (path.StartsWith("/api/v1/issues/", StringComparison.Ordinal) && path.EndsWith("/activity", StringComparison.Ordinal))
                {
                    if (path.Length <= 24 || path[15..^9].Contains('/')) { response.StatusCode = 404; return; }
                    var issue = stream.Issue(path[15..^9]);
                    if (issue is null) { response.StatusCode = 404; return; }
                    if (activity is null) { response.StatusCode = 503; return; }
                    var limit = 50;
                    if (request.Query.Any(q => q.Value.Count != 1 || q.Key is not ("limit" or "after")) ||
                        (request.Query.ContainsKey("limit") && !int.TryParse(request.Query["limit"], out limit)))
                    { response.StatusCode = 400; return; }
                    var result = await activity.ReadAsync(issue, limit,
                        request.Query.ContainsKey("after") ? request.Query["after"].ToString() : null, context.RequestAborted);
                    if (stream.Issue(issue.Id)?.Revision != issue.Revision) { response.StatusCode = 409; return; }
                    await JsonAsync(context, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, DashboardStream.Json));
                }
                else if (path.StartsWith("/api/v1/issues/", StringComparison.Ordinal) && !path[15..].Contains('/'))
                {
                    var id = path[15..];
                    var issue = stream.Issue(id);
                    if (issue is null) response.StatusCode = 404;
                    else
                    {
                        response.Headers.ETag = $"\"{issue.Revision}\"";
                        if (request.Headers.IfNoneMatch == response.Headers.ETag) response.StatusCode = 304;
                        else await JsonAsync(context, await IssueBodyAsync(issueBodies, issue, context.RequestAborted));
                    }
                }
                else response.StatusCode = 404;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (ArgumentException) { response.StatusCode = 400; }
            catch (ActivityConflictException) { response.StatusCode = 409; }
            catch (Exception ex) when (ex is InvalidDataException or CommandStartException or CommandTimeoutException or CommandOutputLimitException)
            { if (!response.HasStarted) response.StatusCode = 503; else context.Abort(); }
            catch (IOException) { context.Abort(); }
            catch (InvalidOperationException)
            {
                if (!response.HasStarted) response.StatusCode = 503;
                else context.Abort();
            }
        });
        try { await app.StartAsync(token); }
        catch { await app.DisposeAsync(); throw; }
        return new DashboardHost(app, stream, actions, runtime, git);
    }

    private static async Task<System.Text.Json.JsonDocument> ReadActionBodyAsync(HttpRequest request, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, token);
            if (count == 0) break;
            if (bytes.Length + count > 64 * 1024) throw new BadHttpRequestException("Request too large.", 413);
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Json.JsonDocument.Parse(bytes.ToArray(), new System.Text.Json.JsonDocumentOptions { MaxDepth = 8 });
    }

    private static Task<byte[]> HistoryBodyAsync(DetailCache<string, byte[]> cache, GitHistory history, int limit, CancellationToken token) =>
        cache.GetAsync(history.Tip + ":" + history.HistoryBoundary + ":" + limit,
            _ => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(history, DashboardStream.Json)), token);

    private static Task<byte[]> ComparisonBodyAsync(DetailCache<string, byte[]> cache, GitComparison comparison, GitPatch? patch, CancellationToken token) =>
        cache.GetAsync($"{comparison.TargetTip}:{comparison.IssueTip}:{comparison.HistoryBoundary}:{patch is not null}",
            _ => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { comparison, patch }, DashboardStream.Json)), token);

    private static Task<byte[]> IssueBodyAsync(DetailCache<string, byte[]> cache, IssueSummary issue, CancellationToken token) =>
        cache.GetAsync(issue.Revision, _ => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(issue, DashboardStream.Json)), token);

    private static async Task JsonAsync(HttpContext context, byte[] bytes)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        if (context.Request.Method != "HEAD") await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task WorktreeEventsAsync(HttpContext context, WorktreeWatch watch, CancellationToken token)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await context.Response.WriteAsync(": connected\n\n", token);
            await context.Response.Body.FlushAsync(token);
            while (!token.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await watch.Reader.WaitToReadAsync(heartbeat.Token)) return;
                    while (watch.Reader.TryRead(out var body))
                    {
                        await context.Response.WriteAsync("event: worktree\ndata: ", token);
                        await context.Response.Body.WriteAsync(body, token);
                        await context.Response.WriteAsync("\n\n", token);
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { await context.Response.WriteAsync(": heartbeat\n\n", token); }
                await context.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task EventsAsync(HttpContext context, DashboardStream stream)
    {
        var after = context.Request.Headers["Last-Event-ID"].ToString();
        if (after.Length == 0) after = context.Request.Query["after"].ToString();
        if (after.Length > 100) { context.Response.StatusCode = 400; return; }
        using var subscription = stream.Subscribe(after);
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(context.RequestAborted);
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        while (!context.RequestAborted.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token)) return;
                while (subscription.Reader.TryRead(out var item))
                {
                    await context.Response.WriteAsync($"id: {item.Id}\nevent: {item.Kind}\ndata: {Encoding.UTF8.GetString(item.Data)}\n\n", context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            { await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted); }
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static Dictionary<string, (string Type, byte[] Bytes)> LoadAssets()
    {
        var assets = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        foreach (var (name, type) in new[] { ("index.html", "text/html; charset=utf-8"), ("dashboard.css", "text/css; charset=utf-8"), ("dashboard.js", "text/javascript; charset=utf-8"), ("dependency-tree.js", "text/javascript; charset=utf-8"), ("timeline.js", "text/javascript; charset=utf-8"), ("inspector-resize.js", "text/javascript; charset=utf-8"), ("issue-relations.js", "text/javascript; charset=utf-8"), ("issue-table.js", "text/javascript; charset=utf-8"), ("issue-filters.js", "text/javascript; charset=utf-8"), ("timeline-model.js", "text/javascript; charset=utf-8"), ("timeline-gl.js", "text/javascript; charset=utf-8") })
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Abacus.Dashboard." + name)
                ?? throw new InvalidOperationException("Bundled dashboard asset missing.");
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            assets[name == "index.html" ? "/" : "/" + name] = (type, bytes.ToArray());
        }
        return assets;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        runtime?.StopControls();
        git?.WorktreeWatches.Complete();
        try { if (actions is not null) await actions.DrainAsync(); }
        finally { await CloseHostAsync(); }
    }

    private async Task CloseHostAsync()
    {
        stream.Complete();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(deadline.Token); }
        finally { await app.DisposeAsync(); }
    }
}
