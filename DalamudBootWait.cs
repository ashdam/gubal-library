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
///         <b>Why a plugin cares about somebody else's setting.</b> With it off, measured 2026-08-11,
///         the redirection is in place 0.98s before the client's first Excel read: a race won, not a
///         barrier. Downloading a pack inside that margin would let the game read English mid-download
///         and cost the session its translation. With it on, the boot waits.
///     </para>
///     <para>
///         <b>All of this is Dalamud's private business, so all of it is optional.</b>
///         <c>DalamudConfiguration</c> is internal and no API exposes the flag, so it is reached
///         through <c>Service&lt;T&gt;</c> by reflection. Every method answers "I could not" rather
///         than throwing: a Dalamud release that renames something turns this feature off and must
///         never turn the plugin off with it.
///     </para>
///     <para>
///         Writes go to the in-memory configuration, never the file — Dalamud saves the whole object
///         whenever anything changes, discarding edits made behind its back. The file is read only as
///         a fallback, where a stale answer is still right: it is what Dalamud loaded at startup.
///     </para>
/// </remarks>
internal static class DalamudBootWait
{
    private const string ConfigTypeName = "Dalamud.Configuration.Internal.DalamudConfiguration";
    private const string ServiceTypeName = "Dalamud.Service`1";
    private const string PropertyName = "IsResumeGameAfterPluginLoad";
    private const string ConfigFileName = "dalamudConfig.json";

    /// <summary>Resolved once and kept, because the window asks on every frame.</summary>
    /// <remarks>
    ///     Static, which is the right lifetime: the object it points at is Dalamud's own and outlives
    ///     the plugin, and a reload gets a fresh load context and so a fresh resolution.
    /// </remarks>
    private static (object Config, PropertyInfo Property)? resolved;

    /// <summary>Whether the boot waits for plugins, or null when it could not be established.</summary>
    /// <remarks>
    ///     Three answers, not two. Callers holding the boot treat null as no — being wrong costs one
    ///     session of English — while the window distinguishes it, because only "off" is fixable.
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
    ///     <b>On only.</b> It is Dalamud's setting and a global one: a plugin may reasonably ask for
    ///     it and may not decide later that somebody has stopped wanting it. <c>QueueSave</c> rather
    ///     than <c>ForceSave</c>, because that is what Dalamud's own settings window does.
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

        // Read back rather than assumed: a property that ignores what it is given is the kind of
        // change this file exists to survive.
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
            // Includes the service not being resolved yet — not expected, and not worth a crash
            // during somebody's boot.
            return false;
        }
    }

    /// <summary>The same flag out of <c>dalamudConfig.json</c>, for when the reflection stops working.</summary>
    /// <remarks>
    ///     The path is derived from this plugin's own config file, which sits two levels under the
    ///     Dalamud directory wherever that is. A hardcoded <c>XIVLauncher</c> would be wrong for every
    ///     other launcher.
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
