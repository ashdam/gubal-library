using System.Text.Json;
using CheapLoc;
using GubalLibrary;

internal static class PackVersionChecks
{
    public static void Run()
    {
        const string older = "2026.09.01.0000.0000";
        const string current = "2026.09.15.0000.0000";
        const string errorKey = "Pack.GameVersionMismatch";
        const string unknownKey = "Pack.GameVersionUnknown";

        var fallback = PackVersion.Error(older, current);
        var unknownFallback = PackVersion.Error(null, current);
        try
        {
            foreach (var language in new[] { "en", "es", "it" })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "loc", language + ".json")));
                Loc.Messages = json.RootElement.EnumerateObject().ToDictionary(
                    p => p.Name, p => p.Value.GetProperty("message").GetString()!);

                Check(PackVersion.Error(current, current) is null, "Matching versions need no error");
                Check(Loc.Messages.ContainsKey(errorKey) && Loc.Messages.ContainsKey(unknownKey),
                    language + " includes both version messages");

                var error = PackVersion.Error(older, current);
                Check(error == string.Format(Loc.Messages[errorKey], older, current),
                    language + " uses the localized error");
                Check(error!.Contains(older) && error.Contains(current), "Both versions are visible");
                Check(PackVersion.Error(current, older) == string.Format(Loc.Messages[errorKey], current, older),
                    "A newer pack also produces an error");

                foreach (var unknown in new string?[] { null, "", " " })
                {
                    Check(PackVersion.Error(unknown, current) == Loc.Messages[unknownKey], "Unknown pack version");
                    Check(PackVersion.Error(current, unknown) == Loc.Messages[unknownKey], "Unknown game version");
                    Check(PackVersion.Error(unknown, unknown) == Loc.Messages[unknownKey], "Both versions unknown");
                }

                Check(language == "en" ? error == fallback : error != fallback, "Localized version error");
                Check(language == "en" ? Loc.Messages[unknownKey] == unknownFallback : Loc.Messages[unknownKey] != unknownFallback,
                    "Localized unknown-version error");
            }
        }
        finally
        {
            Loc.Messages = [];
        }

        Console.WriteLine("Pack version checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
