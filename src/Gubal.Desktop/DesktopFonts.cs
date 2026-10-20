using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GubalDesktop;

internal sealed class DesktopFonts : IDisposable
{
    private const uint PrivateFont = 0x10;
    private readonly PrivateFontCollection fonts = new();
    private readonly string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "NotoSans-Regular.ttf");
    public Font Default { get; }

    public DesktopFonts()
    {
        // WinForms uses GDI for controls and GDI+ for drawing.
        fonts.AddFontFile(path);
        if (AddFontResourceEx(path, PrivateFont, IntPtr.Zero) == 0)
        {
            fonts.Dispose();
            throw new IOException("Could not load the Desktop font.");
        }
        Default = new Font(fonts.Families[0], 11);
    }

    public void Dispose()
    {
        Default.Dispose();
        fonts.Dispose();
        RemoveFontResourceEx(path, PrivateFont, IntPtr.Zero);
    }

    [DllImport("gdi32.dll", EntryPoint = "AddFontResourceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);

    [DllImport("gdi32.dll", EntryPoint = "RemoveFontResourceExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveFontResourceEx(string file, uint flags, IntPtr reserved);
}
