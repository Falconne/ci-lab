using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.AutoMerge;
using Mergician.Services.Database;
using Mergician.Services.GitLab;
using Microsoft.AspNetCore.Authentication;
using Serilog;
using Serilog.Events;
using Util;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

try
{
    // Bind Mergician settings from configuration
    var mergicianSettings = new MergicianSettings();
    builder.Configuration.GetSection("Mergician").Bind(mergicianSettings);

    if (mergicianSettings.GitLab.Url.IsEmpty())
    {
        throw new InvalidOperationException(
            "Mergician:GitLab:Url is not configured. Set it via appsettings.json or the Mergician__GitLab__Url environment variable.");
    }

    if (mergicianSettings.Database.Host.IsEmpty())
    {
        throw new InvalidOperationException(
            "Mergician:Database:Host is not configured. Set it via appsettings.json or the Mergician__Database__Host environment variable.");
    }

    builder.Services.AddSingleton(mergicianSettings);
    builder.Services.AddSingleton(mergicianSettings.Database);
    builder.Services.AddSingleton<DatabaseMigrationService>();
    builder.Services.AddSingleton<HealthService>();

    // Register database services
    builder.Services.AddSingleton<IDbConnectionFactory>(
        new NpgsqlConnectionFactory(mergicianSettings.Database));

    builder.Services.AddSingleton<IMergeGroupRepository, MergeGroupRepository>();

    // Compute GitLab API base URL once at startup from configuration
    var gitlabApiBaseUrl = $"{mergicianSettings.GitLab.ServerUrl.TrimEnd('/')}/api/v4";

    // Register HttpClient factory and GitLab services
    builder.Services.AddHttpClient("GitLabOAuth")
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            if (mergicianSettings.GitLab.AllowInsecureSsl)
            {
                // TLS validation disabled via config — for self-signed certs in dev/internal environments only
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }

            return handler;
        });

    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<ExternalServiceRateLimiter>();
    builder.Services.AddSingleton<GitLabOAuthService>();
    builder.Services.AddSingleton<GitLabApiClient>();
    builder.Services.AddSingleton<CacheService<int, GitLabProject>>();
    builder.Services.AddSingleton<GitLabService>();
    builder.Services.AddSingleton<GitLabPipelineService>();
    builder.Services.AddSingleton<DeadBranchesService>();
    builder.Services.AddSingleton<AutoMergeGitLabApiService>();
    builder.Services.AddSingleton<MergeRequestLookupService>();
    builder.Services.AddSingleton<MergeGroupManagementService>();
    builder.Services.AddSingleton<MergePermissionService>();
    builder.Services.AddSingleton<VersionService>();

    // Register background user activity sync service
    builder.Services.AddSingleton<UserActivityBackgroundSyncService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UserActivityBackgroundSyncService>());

    // Register GitLab authentication handler
    builder.Services.AddSingleton(new GitLabAuthSettings { ApiBaseUrl = gitlabApiBaseUrl });
    builder.Services.AddAuthentication(GitLabCookieAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, GitLabCookieAuthenticationHandler>(
            GitLabCookieAuthenticationHandler.SchemeName,
            null);

    builder.Services.AddAuthorization();

    // GitLabUserFactory is needed for service user access (background tasks)
    builder.Services.AddSingleton(
        new GitLabUserFactory(
            gitlabApiBaseUrl,
            mergicianSettings.GitLab.ServiceToken));

    // Register startup service (runs health checks before marking app as ready)
    builder.Services.AddSingleton<GitLabRecoveryService>();
    builder.Services.AddSingleton<StartupAndRecoveryService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StartupAndRecoveryService>());

    // Register background cleanup service
    builder.Services.AddHostedService<CleanupService>();

    // Register auto merge background service
    builder.Services.AddHostedService<AutoMergeService>();

    // Add services
    builder.Services.AddControllers();

    // Configure CORS for native development (Vue dev server on different port)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
    });

    var app = builder.Build();

    Log.Information("GitLab API base URL: {GitLabApiBaseUrl}", gitlabApiBaseUrl);

    app.UseSerilogRequestLogging(options =>
    {
        options.GetLevel = (_, _, _) => LogEventLevel.Debug;
    });

    app.UseCors();

    // ---------------------------------------------------------------------
    // Startup gating middleware
    //
    // When the application is still performing its initial health checks and
    // migrations, we mark it as "not ready" via StartupAndRecoveryService.  Any client
    // requests during that period should not be forwarded to the normal
    // controllers because many services (database, GitLab) may be unavailable
    // and would return errors. Instead we intercept API calls here and return
    // a 503 Service Unavailable with the current startup status.  This allows
    // the frontend to detect a restart and display the startup overlay, and
    // avoids spamming the backend with failing requests while it's booting.
    //
    // The endpoint /api/health itself is excluded so the status can be
    // polled unconditionally.
    // ---------------------------------------------------------------------
    app.Use(async (context, next) =>
    {
        var startupStateService = context.RequestServices.GetRequiredService<HealthService>();

        if (!context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/api/health"))
        {
            try
            {
                await next();
            }
            catch (GitLabStartupRequiredException ex) when (!context.Response.HasStarted)
            {
                Log.Error(
                    ex,
                    "GitLab call {OperationName} forced the application back into startup mode (from exempted path)",
                    ex.OperationName);

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(startupStateService.GetStatus());
            }

            return;
        }

        var startupStatus = startupStateService.GetStatus();

        if (startupStatus.IsReady)
        {
            try
            {
                await next();
            }
            catch (GitLabStartupRequiredException ex) when (!context.Response.HasStarted)
            {
                Log.Error(
                    ex,
                    "GitLab call {OperationName} forced the application back into startup mode",
                    ex.OperationName);

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(startupStateService.GetStatus());
            }

            return;
        }

        Log.Information(
            "Rejecting request to {Path} because startup is still in progress: {Message}",
            context.Request.Path,
            startupStatus.Message);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(startupStatus);
    });

    // Authentication and authorization middleware
    app.UseAuthentication();
    app.UseAuthorization();

    // Serve static files from wwwroot (for production builds of the Vue frontend)
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();

    // Fallback to index.html for SPA routing (must be after MapControllers)
    app.MapFallbackToFile("index.html");

    var versionService = app.Services.GetRequiredService<VersionService>();
    Log.Information(
        "Mergician v{Version} starting on {Urls}",
        versionService.GetVersion(),
        string.Join(", ", app.Urls));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Mergician terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}