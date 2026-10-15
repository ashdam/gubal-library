using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;

namespace GubalLibrary;

internal static class DalamudBootWait
{
    private const string ConfigTypeName = "Dalamud.Configuration.Internal.DalamudConfiguration";
    private const string ServiceTypeName = "Dalamud.Service`1";
    private const string PropertyName = "IsResumeGameAfterPluginLoad";
    private const string ConfigFileName = "dalamudConfig.json";

    private static (object Config, PropertyInfo Property)? resolved;

    public static bool? IsOn(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            if (Configuration(out var config, out var property) && property.GetValue(config) is bool live)
                return live;
        }
        catch (Exception) { return null; }

        return FromFile(pluginInterface);
    }

    public static bool EnsureOn(IDalamudPluginInterface pluginInterface)
    {
        if (IsOn(pluginInterface) is true) return true;
        try
        {
            if (!Configuration(out var config, out var property) || !property.CanWrite) return false;
            var save = config.GetType().GetMethod("QueueSave", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (save is null) return false;
            property.SetValue(config, true);
            save.Invoke(config, null);
            return IsOn(pluginInterface) is true;
        }
        catch (Exception) { return false; }
    }

    private static bool Configuration(out object config, out PropertyInfo property)
    {
        if (resolved is { } cached)
        {
            (config, property) = cached;
            return true;
        }

        config = null!;
        property = null!;

        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;

            if (dalamud.GetType(ConfigTypeName) is not { } configType
                || dalamud.GetType(ServiceTypeName) is not { } serviceType)
            {
                return false;
            }

            var get = serviceType.MakeGenericType(configType)
                .GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (get?.Invoke(null, null) is not { } instance
                || configType.GetProperty(PropertyName) is not { CanRead: true, PropertyType: var valueType } found
                || valueType != typeof(bool))
            {
                return false;
            }

            config = instance;
            property = found;
            resolved = (instance, found);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool? FromFile(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            if (pluginInterface.ConfigFile.Directory?.Parent is not { } dalamudDirectory)
            {
                return null;
            }

            var path = Path.Combine(dalamudDirectory.FullName, ConfigFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);

            return document.RootElement.TryGetProperty(PropertyName, out var value)
                   && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
