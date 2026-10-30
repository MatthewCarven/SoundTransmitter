using NAudio.CoreAudioApi;

namespace SoundTransmitter.Core;

/// Finding playback devices ("render endpoints") by name.
public static class Endpoints
{
    static readonly MMDeviceEnumerator Enumerator = new();

    public static List<MMDevice> ActiveRender() =>
        Enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    public static MMDevice? DefaultRender()
    {
        try { return Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// The default device for null; otherwise the one active device whose name
    /// matches exactly, or failing that the only one containing the text.
    public static MMDevice Find(string? namePart)
    {
        if (string.IsNullOrWhiteSpace(namePart))
            return DefaultRender() ?? throw new InvalidOperationException("no default playback device");

        var all = ActiveRender();
        var exact = all.Where(d => string.Equals(d.FriendlyName, namePart, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var partial = all.Where(d => d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1) return partial[0];

        string list = string.Join(Environment.NewLine, all.Select(d => "  " + d.FriendlyName));
        throw new InvalidOperationException(partial.Count == 0
            ? $"no active playback device matches \"{namePart}\". Active devices:{Environment.NewLine}{list}"
            : $"\"{namePart}\" matches {partial.Count} devices; be more specific:{Environment.NewLine}{list}");
    }

    public static string Describe(MMDevice device)
    {
        var f = device.AudioClient.MixFormat;
        return $"{device.FriendlyName}  ({f.Channels} ch, {f.SampleRate} Hz, {f.BitsPerSample}-bit {f.Encoding})";
    }

    public static void PrintList()
    {
        string? defaultId = DefaultRender()?.ID;
        foreach (var d in ActiveRender())
            Console.WriteLine($"{(d.ID == defaultId ? "* " : "  ")}{Describe(d)}");
        Console.WriteLine("(* = default)");
    }
}
