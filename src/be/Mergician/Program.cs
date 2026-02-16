using Mergician.Entities;
using Mergician.Services;
using Mergician.Services.Gitlab;
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

    builder.Services.AddSingleton(mergicianSettings);

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
    builder.Services.AddScoped<GitlabUserFactory>(sp => new GitlabUserFactory(
        sp.GetRequiredService<IHttpContextAccessor>(),
        sp.GetRequiredService<GitLabOAuthService>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        gitlabApiBaseUrl,
        mergicianSettings.GitLab.ServiceToken));

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

    // Serve static files from wwwroot (for production builds of the Vue frontend)
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();

    // Fallback to index.html for SPA routing (must be after MapControllers)
    app.MapFallbackToFile("index.html");

    Log.Information("Mergician starting on {Urls}", string.Join(", ", app.Urls));
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
