namespace Bootstrap.Services.Utilities;

public static class EnvHelper
{
    public static void LoadEnvFile(string envPath)
    {
        if (!File.Exists(envPath))
        {
            LogHelper.Log($"No .env file found at {envPath}");
            return;
        }

        LogHelper.Log($"Loading environment from {envPath}");
        foreach (var line in File.ReadAllLines(envPath))
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

    public static void SaveOrUpdateEnvFile(string envPath, string key, string value)
    {
        var lines = File.Exists(envPath) ? File.ReadAllLines(envPath).ToList() : new List<string>();
        var keyPrefix = $"{key}=";
        var lineIndex = lines.FindIndex(l => l.Trim().StartsWith(keyPrefix));

        var newLine = $"{key}=\"{value}\"";

        if (lineIndex >= 0)
        {
            lines[lineIndex] = newLine;
            LogHelper.Log($"Updated {key} in .env file");
        }
        else
        {
            lines.Add(newLine);
            LogHelper.Log($"Added {key} to .env file");
        }

        File.WriteAllLines(envPath, lines);
        Environment.SetEnvironmentVariable(key, value);
    }
}