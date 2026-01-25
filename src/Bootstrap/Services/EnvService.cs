using Serilog;

namespace Bootstrap.Services;

public class EnvService
{
    public string EnvPath { get; }

    public EnvService(string envPath)
    {
        EnvPath = envPath;
    }

    public void LoadEnvFile()
    {
        if (!File.Exists(EnvPath))
        {
            Log.Information($"No .env file found at {EnvPath}");
            return;
        }

        Log.Information($"Loading environment from {EnvPath}");
        foreach (var line in File.ReadAllLines(EnvPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim().Trim('"');
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    public void SaveOrUpdateEnvFile(string key, string value)
    {
        var lines = File.Exists(EnvPath) ? File.ReadAllLines(EnvPath).ToList() : new List<string>();
        var keyPrefix = $"{key}=";
        var lineIndex = lines.FindIndex(l => l.Trim().StartsWith(keyPrefix));

        var newLine = $"{key}=\"{value}\"";

            if (lineIndex >= 0)
            {
                lines[lineIndex] = newLine;
                Log.Information($"Updated {key} in .env file");
            }
            else
            {
                lines.Add(newLine);
                Log.Information($"Added {key} to .env file");
            }

        File.WriteAllLines(EnvPath, lines);
        Environment.SetEnvironmentVariable(key, value);
    }
}
