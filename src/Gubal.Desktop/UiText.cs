using System.Globalization;
using System.Resources;

namespace GubalDesktop;

internal static class UiText
{
    private static readonly ResourceManager Resources = new("GubalDesktop.Strings", typeof(UiText).Assembly);

    public static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new MissingManifestResourceException(name);

    public static string Format(string name, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(name), values);
}
