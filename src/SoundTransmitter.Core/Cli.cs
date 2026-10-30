using System.Globalization;

namespace SoundTransmitter.Core;

/// Timestamped lines to the console and, optionally, a log file.
public static class Log
{
    static readonly object Gate = new();
    static StreamWriter? file;

    public static void ToFile(string path)
    {
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file = new StreamWriter(stream) { AutoFlush = true };
    }

    public static void Line(string message)
    {
        string text = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            Console.WriteLine(text);
            file?.WriteLine(text);
        }
    }
}

/// "--name value" and "--flag" options. Anything unrecognised is an error.
public sealed class Args
{
    readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv, string[] valueOptions, string[] flagOptions)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            string a = argv[i];
            if (!a.StartsWith("--")) throw new ArgumentException($"unexpected argument \"{a}\"");
            string name = a[2..];
            if (flagOptions.Contains(name, StringComparer.OrdinalIgnoreCase)) values[name] = null;
            else if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= argv.Length) throw new ArgumentException($"--{name} needs a value");
                values[name] = argv[++i];
            }
            else throw new ArgumentException($"unknown option --{name}");
        }
    }

    public bool Has(string name) => values.ContainsKey(name);
    public string? Get(string name) => values.TryGetValue(name, out var v) ? v : null;
    public int Int(string name, int fallback) =>
        Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public double Double(string name, double fallback) =>
        Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
