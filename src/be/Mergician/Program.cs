using Mergician.Services;
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
    builder.Services.AddSingleton(mergicianSettings);

    // Register HttpClient factory and GitLab OAuth service
    builder.Services.AddHttpClient("GitLabOAuth")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
    builder.Services.AddSingleton<GitLabOAuthService>();

    // Add services
    builder.Services.AddControllers();

    // Configure CORS for development (frontend on different port)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
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
