using System.Text;

namespace SignIt.Infrastructure.Configuration;

// Minimal .env loader for local development only. The file is optional, gitignored and never
// overrides variables that already exist, so deployed configuration always wins.
public static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;
            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (value.StartsWith('"'))
                value = value.Length > 1 && value.EndsWith('"')
                    ? value[1..^1]
                    : ReadQuoted(reader, value[1..]);
            if (key.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string ReadQuoted(StreamReader reader, string firstLine)
    {
        var builder = new StringBuilder(firstLine);
        while (reader.ReadLine() is { } line)
        {
            if (line.EndsWith('"'))
            {
                builder.Append('\n').Append(line[..^1]);
                break;
            }
            builder.Append('\n').Append(line);
        }
        return builder.ToString();
    }
}
