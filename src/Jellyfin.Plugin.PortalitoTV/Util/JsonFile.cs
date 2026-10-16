using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.PortalitoTV.Util;

/// <summary>Atomic read/write of the plugin's JSON files.</summary>
public static class JsonFile
{
    /// <summary>The serializer options used for every file.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads a file.</summary>
    /// <typeparam name="T">The type stored.</typeparam>
    /// <param name="path">File path.</param>
    /// <returns>The value, or null if the file is missing or unreadable.</returns>
    public static T? Load<T>(string path)
        where T : class
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a file through a temporary one, so a crash never leaves it half written.</summary>
    /// <typeparam name="T">The type stored.</typeparam>
    /// <param name="path">File path.</param>
    /// <param name="value">The value.</param>
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, value, Options);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
