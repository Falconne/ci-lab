using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();

    // Add services
    builder.Services.AddControllers();

    // Configure CORS for development (frontend on different port)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
                .AllowAnyHeader()
                .AllowAnyMethod();
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
