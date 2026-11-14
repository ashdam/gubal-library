using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private static readonly HashSet<string> PictureResources =
        new(typeof(Plugin).Assembly.GetManifestResourceNames(), StringComparer.Ordinal);
    private static readonly Dictionary<string, string[]> PictureExamples = new(StringComparer.Ordinal);
    private string? selectedPartCategory;

    private void DrawPartSelection(PackContents pack, ref bool changed)
    {
        using var body = ImRaii.Child("##localizationParts", Vector2.Zero,
            false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!body) return;

        var selected = pack.Layout.FirstOrDefault(group => CategoryKey(group) == this.selectedPartCategory)
            ?? pack.Layout[0];
        this.selectedPartCategory = CategoryKey(selected);

        if (ImGui.GetContentRegionAvail().X < 680f * ImGuiHelpers.GlobalScale)
        {
            var categories = pack.Layout.Select(group => group.Name).ToArray();
            var selectedIndex = 0;
            for (var i = 0; i < pack.Layout.Count; i++) if (pack.Layout[i] == selected) selectedIndex = i;
            if (PluginUi.ComboBox("##partCategory", ref selectedIndex, categories))
            {
                selected = pack.Layout[selectedIndex];
                this.selectedPartCategory = CategoryKey(selected);
            }
            using var options = ImRaii.Child("##partOptions");
            if (options) this.DrawPartCategory(selected, ref changed);
            return;
        }

        using var columns = ImRaii.Table("##partCategories", 2, ImGuiTableFlags.SizingStretchProp);
        if (!columns) return;
        var labels = pack.Layout.Select(group =>
        {
            var on = group.Parts.Count(part => !part.Sheets.Any(this.config.DisabledSheets.Contains));
            return $"{group.Name}  ({on}/{group.Parts.Length})";
        }).ToArray();
        var sidebarWidth = labels.Max(label => ImGui.CalcTextSize(label).X) + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.TableSetupColumn("##categories", ImGuiTableColumnFlags.WidthFixed, sidebarWidth);
        ImGui.TableSetupColumn("##options", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var columnSize = new Vector2(0, Math.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().CellPadding.Y));
        using (var sidebar = ImRaii.Child("##categoryList", columnSize))
        {
            if (sidebar)
            {
                for (var i = 0; i < pack.Layout.Count; i++)
                {
                    var group = pack.Layout[i];
                    if (PluginUi.Selectable($"{labels[i]}##category_{i}", group == selected, Vector2.Zero))
                    {
                        selected = group;
                        this.selectedPartCategory = CategoryKey(group);
                    }
                }
            }
        }
        ImGui.TableSetColumnIndex(1);
        using var content = ImRaii.Child("##partOptions", columnSize);
        if (content) this.DrawPartCategory(selected, ref changed);
    }

    private static string CategoryKey(GroupView group) => group.Id;

    private void DrawPartCategory(GroupView group, ref bool changed)
    {
        var active = group.Parts.Count(part => !part.Sheets.Any(this.config.DisabledSheets.Contains));
        var all = active == group.Parts.Length;
        var categoryChanged = DrawPartSwitch(group.Name, "category", ref all, alignRight: true, heading: true);
        if (categoryChanged)
        {
            this.SetSheets(group.Parts.SelectMany(part => part.Sheets), all);
            active = all ? group.Parts.Length : 0;
            changed = true;
        }
        if (active > 0 && active < group.Parts.Length)
            ImGui.TextColored(Amber, string.Format(Loc.Localize("Parts.Count", "{0} of {1} ON"), active, group.Parts.Length));
        using (ImRaii.PushColor(ImGuiCol.Text, Grey)) ImGui.TextWrapped(group.Description);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        foreach (var view in group.Parts)
        {
            var key = view.Part.Id;
            using var id = ImRaii.PushId(key);
            var on = !view.Sheets.Any(this.config.DisabledSheets.Contains);
            if (DrawPartSwitch(view.Part.Name, "part", ref on, alignRight: true))
            {
                this.SetSheets(view.Sheets, on);
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(view.SheetList);
            using (ImRaii.PushColor(ImGuiCol.Text, Grey)) ImGui.TextWrapped(view.Part.Description);
            this.DrawPartExamples(group.Id, view.Part.Id);
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, Grey))
            using (ImRaii.PushFont(UiBuilder.MonoFont))
                ImGui.TextWrapped($"{Loc.Localize("Parts.Sheets", "Sheets")}: {view.SheetList}");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }
    }

    // A switch is enabled only when all its sheets are enabled.
    private static bool DrawPartSwitch(string label, string id, ref bool on, bool alignRight = false, bool heading = false)
    {
        var height = ImGui.GetFrameHeight();
        var width = height * 1.8f;
        var start = ImGui.GetCursorScreenPos();
        var rowStart = start;
        var labelBottom = start.Y;
        var labelPressed = false;
        if (alignRight)
        {
            var available = ImGui.GetContentRegionAvail().X;
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1f, available - width - ImGui.GetStyle().ItemSpacing.X));
            ImGui.AlignTextToFramePadding();
            if (heading) PluginUi.DrawSubheading(label);
            else ImGui.TextWrapped(label);
            ImGui.PopTextWrapPos();
            labelBottom = ImGui.GetItemRectMax().Y;
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                labelPressed = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
                if (labelPressed) on = !on;
            }
            start.X += Math.Max(0f, available - width);
            ImGui.SetCursorScreenPos(start);
        }
        var pressed = PluginUi.Toggle($"##switch_{id}", ref on);
        if (alignRight)
        {
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X,
                Math.Max(labelBottom, start.Y + height) + ImGui.GetStyle().ItemSpacing.Y));
            return pressed || labelPressed;
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextWrapped(label);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                on = !on;
                pressed = true;
            }
        }
        return pressed;
    }

    private void DrawPartExamples(string group, string part)
    {
        var names = ExampleNames($"{group}.{part}");
        if (names.Length == 0) names = ExampleNames(group);
        foreach (var name in names)
        {
            if (this.Picture(name) is not { } example) continue;
            var ratio = Math.Min(1f, Math.Max(1f, ImGui.GetContentRegionAvail().X) / example.Width);
            ImGui.Spacing();
            ImGui.Image(example.Handle, new Vector2(example.Width, example.Height) * ratio);
        }
    }

    private static string[] ExampleNames(string name)
    {
        if (PictureExamples.TryGetValue(name, out var names)) return names;
        const string resourcePrefix = "GubalLibrary.images.tooltips.";
        var prefix = $"{resourcePrefix}{name}";
        names = PictureResources.Where(resource => resource == $"{prefix}.png"
                || (resource.StartsWith(prefix + ".", StringComparison.Ordinal) && resource.EndsWith(".png", StringComparison.Ordinal)
                    && uint.TryParse(resource[(prefix.Length + 1)..^4], out _)))
            .OrderBy(resource => resource == $"{prefix}.png" ? 0u : uint.Parse(resource[(prefix.Length + 1)..^4]))
            .Select(resource => resource[resourcePrefix.Length..^4]).ToArray();
        PictureExamples.Add(name, names);
        return names;
    }

    private static bool HasPicture(string name) => PictureResources.Contains($"GubalLibrary.images.tooltips.{name}.png");
}
