using Serilog;

namespace Bootstrap.Services.Utilities;

public static class Logging
{
    public static void Init()
    {
        var logDir = Directory.GetCurrentDirectory();
        var logPath = Path.Combine(logDir, "f.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 1,
                shared: true)
            .CreateLogger();
    }

    public static void LogSeparator(int width = 60, char character = '=')
    {
        Log.Information(new string(character, width));
    }

    public static void LogSection(string title)
    {
        LogSeparator();
        Log.Information(title);
        LogSeparator();
    }
}