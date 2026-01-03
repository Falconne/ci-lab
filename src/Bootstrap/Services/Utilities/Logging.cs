using Serilog;

namespace Bootstrap.Services.Utilities;

public static class Logging
{
    private static readonly ILogger _logger;

    static Logging()
    {
        var logDir = Directory.GetCurrentDirectory();
        var logPath = Path.Combine(logDir, "f.log");

        _logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Infinite,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 1,
                shared: true)
            .CreateLogger();
    }

    public static void Log(string message) => _logger.Information(message);

    public static void LogInfo(string message, int indent = 0) =>
        _logger.Information("{Indent}{Message}", new string(' ', indent * 2), message);

    public static void LogWarning(string message, int indent = 0) =>
        _logger.Warning("{Indent} WARNING: {Message}", new string(' ', indent * 2), message);

    public static void LogError(string message, int indent = 0) =>
        _logger.Error("{Indent} ERROR: {Message}", new string(' ', indent * 2), message);

    public static void LogSuccess(string message, int indent = 0) =>
        _logger.Information("{Indent} ? {Message}", new string(' ', indent * 2), message);

    public static void LogSeparator(int width = 60, char character = '=') =>
        _logger.Information(new string(character, width));

    public static void LogSection(string title)
    {
        LogSeparator();
        Log(title);
        LogSeparator();
    }
}
