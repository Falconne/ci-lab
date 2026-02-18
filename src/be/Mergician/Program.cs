using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Authentication;
using Mergician.Services.Database;
using Mergician.Services.Gitlab;
using Microsoft.AspNetCore.Authentication;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Bind Mergician settings from configuration
    var mergicianSettings = new MergicianSettings();
    builder.Configuration.GetSection("Mergician").Bind(mergicianSettings);

    if (string.IsNullOrWhiteSpace(mergicianSettings.GitLab.Url))
        throw new InvalidOperationException(
            "Mergician:GitLab:Url is not configured. Set it via appsettings.json or the Mergician__GitLab__Url environment variable.");

    if (string.IsNullOrWhiteSpace(mergicianSettings.Database.Host))
        throw new InvalidOperationException(
            "Mergician:Database:Host is not configured. Set it via appsettings.json or the Mergician__Database__Host environment variable.");

    builder.Services.AddSingleton(mergicianSettings);
    builder.Services.AddSingleton(mergicianSettings.Database);

    // Run database migrations
    Log.Information("Running database migrations");
    var migrationService = new DatabaseMigrationService(mergicianSettings.Database);
    migrationService.MigrateDatabase();
    Log.Information("Database migrations completed");

    // Register database services
    builder.Services.AddSingleton<IDbConnectionFactory>(new NpgsqlConnectionFactory(mergicianSettings.Database));
    builder.Services.AddSingleton<IMergicianRepository, MergicianRepository>();

    // Compute GitLab API base URL once at startup from configuration
    var gitlabApiBaseUrl = $"{mergicianSettings.GitLab.ServerUrl.TrimEnd('/')}/api/v4";
    Log.Information("GitLab API base URL: {GitLabApiBaseUrl}", gitlabApiBaseUrl);

    // Register HttpClient factory and GitLab services
    builder.Services.AddHttpClient("GitLabOAuth")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<GitLabOAuthService>();
    builder.Services.AddSingleton(new CacheService<int, GitLabProject>());
    builder.Services.AddSingleton<GitlabService>();
    builder.Services.AddSingleton<VersionService>();
    builder.Services.AddScoped<GitlabActivityService>();

    // Register GitLab authentication handler
    builder.Services.AddSingleton(new GitLabAuthSettings { ApiBaseUrl = gitlabApiBaseUrl });
    builder.Services.AddAuthentication(GitLabCookieAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, GitLabCookieAuthenticationHandler>(
            GitLabCookieAuthenticationHandler.SchemeName, null);
    builder.Services.AddAuthorization();

    // GitlabUserFactory is still needed for service user access (background tasks, health checks)
    builder.Services.AddSingleton(new GitlabUserFactory(
        gitlabApiBaseUrl,
        mergicianSettings.GitLab.ServiceToken));

    // Register background cleanup service
    builder.Services.AddHostedService<CleanupService>();

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

    app.UseSerilogRequestLogging();
    app.UseCors();

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
    Log.Information("Mergician v{Version} starting on {Urls}", versionService.GetVersion(), string.Join(", ", app.Urls));
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
