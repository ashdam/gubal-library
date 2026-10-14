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
        using var body = ImRaii.Child("##localizationParts", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2),
            false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!body) return;

        var selected = pack.Layout.FirstOrDefault(group => CategoryKey(group) == this.selectedPartCategory)
            ?? pack.Layout[0];
        this.selectedPartCategory = CategoryKey(selected);

        if (ImGui.GetContentRegionAvail().X < 680f * ImGuiHelpers.GlobalScale)
        {
            if (ImGui.BeginCombo("##partCategory", selected.Name))
            {
                foreach (var group in pack.Layout)
                {
                    if (ImGui.Selectable(group.Name, group == selected))
                    {
                        selected = group;
                        this.selectedPartCategory = CategoryKey(group);
                    }
                }
                ImGui.EndCombo();
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
                    if (ImGui.Selectable($"{labels[i]}##category_{i}", group == selected))
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
        ImGui.SetWindowFontScale(1.25f);
        bool categoryChanged;
        try
        {
            categoryChanged = DrawPartSwitch(group.Name, "category", ref all, alignRight: true);
        }
        finally
        {
            ImGui.SetWindowFontScale(1f);
        }
        if (categoryChanged)
        {
            this.SetSheets(group.Parts.SelectMany(part => part.Sheets), all);
            active = all ? group.Parts.Length : 0;
            changed = true;
        }
        if (active > 0 && active < group.Parts.Length)
            ImGui.TextColored(Amber, string.Format(Loc.Localize("Parts.Count", "{0} of {1} on"), active, group.Parts.Length));
        using (ImRaii.PushColor(ImGuiCol.Text, Grey)) ImGui.TextWrapped(group.Description);
        if (group.Warning is { Length: > 0 }) ImGui.TextColored(Amber, group.Warning);
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
            if (view.Part.Warning is { Length: > 0 } warning)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, Amber)) ImGui.TextWrapped(warning);
            }
            this.DrawPartExamples(view, view.Part.Image ?? group.Image);
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
    private static bool DrawPartSwitch(string label, string id, ref bool on, bool alignRight = false)
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
            ImGui.TextWrapped(label);
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
        bool pressed;
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4.Zero))
            pressed = ImGui.Button($"##switch_{id}", new Vector2(width, height));
        if (pressed) on = !on;
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var color = on ? Green with { W = hovered ? 0.8f : 0.6f }
            : ImGui.GetStyle().Colors[(int)(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg)];
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(color), height / 2);
        var radius = Math.Max(1f, height / 2 - 3f * ImGuiHelpers.GlobalScale);
        draw.AddCircleFilled(start + new Vector2(on ? width - height / 2 : height / 2, height / 2),
            radius, ImGui.GetColorU32(ImGuiCol.Text));
        if (ImGui.IsItemFocused())
            draw.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(Blue), height / 2);
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

    private void DrawPartExamples(PartView view, string? primary)
    {
        foreach (var name in new[] { primary }.Concat(view.Part.AdditionalImages ?? []))
        {
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var exampleName in ExampleNames(name))
            {
                if (this.Picture(exampleName) is not { } example) continue;
                var ratio = Math.Min(1f, Math.Max(1f, ImGui.GetContentRegionAvail().X) / example.Width);
                ImGui.Spacing();
                ImGui.Image(example.Handle, new Vector2(example.Width, example.Height) * ratio);
            }
        }
    }

    private static string[] ExampleNames(string name)
    {
        if (PictureExamples.TryGetValue(name, out var names)) return names;
        const string resourcePrefix = "GubalLibrary.images.tooltips.";
        var prefix = $"{resourcePrefix}{name}";
        names = PictureResources.Where(resource => resource == $"{prefix}.png"
                || (resource.StartsWith(prefix + "-", StringComparison.Ordinal) && resource.EndsWith(".png", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .Select(resource => resource[resourcePrefix.Length..^4]).ToArray();
        PictureExamples.Add(name, names);
        return names;
    }

    private static bool HasPicture(string name) => PictureResources.Contains($"GubalLibrary.images.tooltips.{name}.png");
}
