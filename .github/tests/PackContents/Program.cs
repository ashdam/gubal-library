using GubalLibrary;

var root = Path.Combine(Path.GetTempPath(), "gubal-layout-test-" + Guid.NewGuid().ToString("N"));
try
{
    var missing = PackContents.Load(root, 259);
    Check(missing.PageCount == 0 && missing.ServableLayouts([]).Count == 0, "Missing pack");

    Write("ui/uld/Test.ULD");
    var layoutOnly = PackContents.Load(root, 259);
    Check(layoutOnly.PageCount == 0 && layoutOnly.PartCount == 0, "A ULD is not an EXD page or a translation part");

    Write("exd/addon_0_en.exd");
    Write("exd/lobby_0_en.exd");
    Write("common/font/Axis_12.fdt");
    var baseline = PackContents.Load(root, 259);
    Write("ui/uld/nested/Another.uld");
    Write("ui/uld/ignored.tex");
    Write("ui/uld/ignored.uld.bak");
    Write("ui/ignored.uld");
    Write("exd/ignored.uld");
    Write("common/font/ignored.uld");
    var contents = PackContents.Load(root, 259);
    var layouts = contents.ServableLayouts([]);
    Check(layouts.Count == 2, "Only ULD files under ui/uld are accepted");
    Check(layouts["UI/ULD/TEST.uld"] == Path.Combine(root, "ui", "uld", "Test.ULD"), "Paths match without case sensitivity");
    Check(layouts.ContainsKey("ui/uld/nested/Another.uld"), "Nested paths use game separators");
    Check(contents.ServableLayouts(["addon"]).Count == 0, "Disabled Addon disables ULD files");
    Check(contents.ServableLayouts(["lobby"]).Count == 2, "Other translation settings do not disable ULD files");
    Check(contents.PageCount == 2 && contents.FontCount == 1, "ULD files do not change page or font counts");
    Check(contents.PartCount == baseline.PartCount, "ULD files do not add checkboxes");
    Check(contents.Servable(["addon"]).Keys.SequenceEqual(["exd/lobby_0_en.exd"]), "Page selection is unchanged");

    var longFile = "ui/uld/" + new string('x', 80) + ".uld";
    Write(longFile);
    var limit = Path.Combine(root, longFile.Replace('/', Path.DirectorySeparatorChar)).Length - 1;
    var limited = PackContents.Load(root, limit);
    Check(limited.TooLong == 1 && limited.ServableLayouts([]).Count == 2, "Long ULD paths are refused");
    Console.WriteLine("Pack contents checks passed.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

void Write(string relative)
{
    var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, []);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
