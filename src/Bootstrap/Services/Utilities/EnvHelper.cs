using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bootstrap.Services.Utilities;

public static class EnvHelper
{
    public static void LoadEnvFile(string envPath)
    {
        if (!File.Exists(envPath))
        {
            Console.WriteLine($"[bootstrap] No .env file found at {envPath}");
            return;
        }

        Console.WriteLine($"[bootstrap] Loading environment from {envPath}");
        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim().Trim('"');
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    public static void SaveOrUpdateEnvFile(string envPath, string key, string value)
    {
        var lines = File.Exists(envPath) ? File.ReadAllLines(envPath).ToList() : new List<string>();
        var keyPrefix = $"{key}=";
        var lineIndex = lines.FindIndex(l => l.Trim().StartsWith(keyPrefix));

        var newLine = $"{key}=\"{value}\"";

        if (lineIndex >= 0)
        {
            lines[lineIndex] = newLine;
            Console.WriteLine($"[bootstrap] Updated {key} in .env file");
        }
        else
        {
            lines.Add(newLine);
            Console.WriteLine($"[bootstrap] Added {key} to .env file");
        }

        File.WriteAllLines(envPath, lines);
        Environment.SetEnvironmentVariable(key, value);
    }
}
