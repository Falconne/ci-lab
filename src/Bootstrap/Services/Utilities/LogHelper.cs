using System;

namespace Bootstrap.Services.Utilities;

public static class LogHelper
{
    private const string Prefix = "[bootstrap]";

    public static void Log(string message) => Console.WriteLine($"{Prefix} {message}");

    public static void LogError(string message, int indent = 0)
    {
        var indentation = new string(' ', indent * 2);
        Console.Error.WriteLine($"{Prefix}{indentation} ERROR: {message}");
    }

    public static void LogWarning(string message, int indent = 0)
    {
        var indentation = new string(' ', indent * 2);
        Console.WriteLine($"{Prefix}{indentation} WARNING: {message}");
    }

    public static void LogInfo(string message, int indent = 0)
    {
        var indentation = new string(' ', indent * 2);
        Console.WriteLine($"{Prefix}{indentation} {message}");
    }

    public static void LogSuccess(string message, int indent = 0)
    {
        var indentation = new string(' ', indent * 2);
        Console.WriteLine($"{Prefix}{indentation} ✓ {message}");
    }

    public static void LogSeparator(int width = 60, char character = '=')
    {
        Console.WriteLine(character.ToString().PadRight(width, character));
    }

    public static void LogSection(string title)
    {
        LogSeparator();
        Log(title);
        LogSeparator();
    }
}
