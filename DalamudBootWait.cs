using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;

namespace GubalLibrary;

/// <summary>
///     Reads and sets Dalamud's "wait for plugins before the game loads", which decides whether an
///     install during startup can work at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a plugin cares about somebody else's setting.</b> Measured on 2026-08-11 with it
///         off: the redirection was in place 0.98s before the client's first Excel read. That margin
///         is a race won, not a barrier — it fits a rename and nothing else. Downloading a pack inside
///         it would let the game read its own English text mid-download, costing the session its
///         translation instead of saving it a restart. With the setting on, the boot waits, and the
///         same download finishes before anything is read.
///     </para>
///     <para>
///         <b>Everything here is Dalamud's private business, so all of it is optional.</b>
///         <c>DalamudConfiguration</c> is internal and no API exposes this flag, so it is reached
///         through <c>Service&lt;T&gt;</c> by reflection. Every method answers "I could not" rather
///         than throwing, and the caller's fallback is to leave the boot alone and say so in the
///         window: a Dalamud release that renames something turns this feature off, and must never
///         turn the plugin off with it.
///     </para>
///     <para>
///         The in-memory configuration is the one written to, never the file. Dalamud holds it for
///         the session and saves the whole object whenever anything changes, so a file edited behind
///         its back is discarded the next time the user touches any Dalamud setting. The file is read
///         only as a fallback for the question, where a stale answer is still the right answer:
///         Dalamud loaded it from there at startup, which is the moment being asked about.
///     </para>
/// </remarks>
internal static class DalamudBootWait
{
    private const string ConfigTypeName = "Dalamud.Configuration.Internal.DalamudConfiguration";
    private const string ServiceTypeName = "Dalamud.Service`1";
    private const string PropertyName = "IsResumeGameAfterPluginLoad";
    private const string ConfigFileName = "dalamudConfig.json";

    /// <summary>Resolved once and kept, because the window asks this question on every frame.</summary>
    /// <remarks>
    ///     Static, and so shared by every instance in this load — which is the right lifetime: the
    ///     configuration object it points at is Dalamud's own and outlives the plugin. A reload gets a
    ///     fresh assembly load context and therefore a fresh resolution, so a stale reference cannot
    ///     survive one.
    /// </remarks>
    private static (object Config, PropertyInfo Property)? resolved;

    /// <summary>Whether the game's boot waits for plugins, or null when it could not be established.</summary>
    /// <remarks>
    ///     Three answers rather than two. Callers deciding whether to hold the boot must treat null as
    ///     no — the cost of being wrong that way is one session of English — while the window says
    ///     something different for "off" than for "I cannot tell", because only one of those is
    ///     something the user can fix.
    /// </remarks>
    public static bool? IsOn(IDalamudPluginInterface pluginInterface)
    {
        if (Configuration(out var config, out var property) && property.GetValue(config) is bool live)
        {
            return live;
        }

        return FromFile(pluginInterface);
    }

    /// <summary>Turns it on, and reports whether that took.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>On only.</b> Nothing here turns it off again: it is Dalamud's setting and a global
    ///         one, so a plugin may reasonably ask for it and may not decide, later and on its own,
    ///         that somebody has stopped wanting it.
    ///     </para>
    ///     <para>
    ///         <c>QueueSave</c> rather than <c>ForceSave</c>: Dalamud's own settings window queues,
    ///         and writing the file from a plugin's frame is a courtesy nobody asked for.
    ///     </para>
    /// </remarks>
    public static bool TryTurnOn()
    {
        if (!Configuration(out var config, out var property))
        {
            return false;
        }

        property.SetValue(config, true);
        config.GetType().GetMethod("QueueSave", BindingFlags.Public | BindingFlags.Instance)?
            .Invoke(config, null);

        // Read back rather than assumed. A property that silently ignores what it is given is exactly
        // the kind of change this whole file is written to survive.
        return property.GetValue(config) is true;
    }

    /// <summary>Resolves Dalamud's live configuration object and the one property wanted from it.</summary>
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
                || configType.GetProperty(PropertyName) is not { CanWrite: true } found)
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
            // Includes the service not being resolved yet, which is not expected — the configuration
            // is what decides which plugins load — but is not worth a crash during somebody's boot.
            return false;
        }
    }

    /// <summary>
    ///     The same flag out of <c>dalamudConfig.json</c>, for when the reflection above stops working.
    /// </summary>
    /// <remarks>
    ///     The path is derived from this plugin's own config file rather than assembled from
    ///     <c>%APPDATA%</c>: <c>…\pluginConfigs\GubalLibrary.json</c> sits two levels under the
    ///     Dalamud directory, wherever that has been put. A hardcoded <c>XIVLauncher</c> would be
    ///     wrong for every launcher that is not the one this was written on.
    /// </remarks>
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
