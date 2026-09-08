using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace S880Tray;

internal sealed class UserSettingsStore(string dataDirectory)
{
    internal string PathName => Path.Combine(dataDirectory, "settings.json");

    internal bool ReadDarkTheme()
    {
        if (!File.Exists(PathName)) return false;
        var settings = JsonNode.Parse(File.ReadAllText(PathName)) as JsonObject ?? throw new InvalidDataException("Settings must contain a JSON object.");
        var theme = settings["theme"]?.GetValue<string>();
        return theme switch { null or "Light" => false, "Dark" => true, _ => throw new InvalidDataException("The saved theme is unsupported.") };
    }

    internal void SaveDarkTheme(bool dark)
    {
        Directory.CreateDirectory(dataDirectory);
        var settings = File.Exists(PathName)
            ? JsonNode.Parse(File.ReadAllText(PathName)) as JsonObject ?? throw new InvalidDataException("Existing settings could not be preserved.")
            : new JsonObject();
        settings["theme"] = dark ? "Dark" : "Light";
        var temporary = Path.Combine(dataDirectory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PathName, true);
            if (ReadDarkTheme() != dark) throw new IOException("The saved theme could not be verified.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
