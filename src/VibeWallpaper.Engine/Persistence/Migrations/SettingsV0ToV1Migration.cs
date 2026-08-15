using System.Text.Json.Nodes;

namespace VibeWallpaper.Engine.Persistence.Migrations;

public sealed class SettingsV0ToV1Migration : IPersistenceMigration
{
    public int FromVersion => 0;
    public int ToVersion => 1;

    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var migrated = (JsonObject)document.DeepClone();
        Add(migrated, "startWithWindows", false);
        Add(migrated, "theme", 0);
        Add(migrated, "interactionHotkey", "Ctrl+Alt+I");
        Add(migrated, "suspendOnFullscreen", true);
        Add(migrated, "suspendOnMaximized", false);
        Add(migrated, "suspendOnRemoteDesktop", true);
        Add(migrated, "suspendOnSessionLock", true);
        Add(migrated, "suspendOnDisplayOff", true);
        Add(migrated, "suspendOnSystemSleep", true);
        Add(migrated, "batteryTargetFps", 30);
        Add(migrated, "batterySaverTargetFps", 15);
        Add(migrated, "incompatibleThrottle", 0);
        AddNull(migrated, "fallbackWallpaper");
        Add(migrated, "fallbackColor", "#101014");
        Add(migrated, "defaultFit", 0);
        Add(migrated, "defaultTargetFps", 30);
        Add(migrated, "defaultAudioEnabled", false);
        Add(migrated, "defaultVolumePercent", 0);
        Add(migrated, "defaultInteractionEnabled", false);
        AddNull(migrated, "managementWindow");
        migrated["schemaVersion"] = 1;
        return migrated;
    }

    private static void Add(JsonObject document, string name, JsonNode value)
    {
        if (!document.ContainsKey(name)) document[name] = value;
    }

    private static void AddNull(JsonObject document, string name)
    {
        if (!document.ContainsKey(name)) document[name] = null;
    }
}
