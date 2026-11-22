using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private enum LibrarySection { Pack, Localization, Report, Help, Developer }

    private (LibrarySection Section, FontAwesomeIcon Icon, string Label)[] LibrarySections =>
    [
        (LibrarySection.Pack, FontAwesomeIcon.BookOpen, Loc.Localize("Tab.Pack", "Language pack")),
        (LibrarySection.Localization, FontAwesomeIcon.SlidersH, Loc.Localize("Tab.Parts", "Localization settings")),
        (LibrarySection.Report, FontAwesomeIcon.Expand, Loc.Localize("Tab.IssueReporting", "Report an issue")),
        (LibrarySection.Help, FontAwesomeIcon.QuestionCircle, Loc.Localize("Tab.Help", "Help and contact")),
        (LibrarySection.Developer, FontAwesomeIcon.Wrench, Loc.Localize("Library.DeveloperTools", "Developer tools")),
    ];

    private void DrawLibrary(PageStatus pages, ref bool changed)
    {
        if (!this.DevToolsEnabled && this.selectedSection == LibrarySection.Developer)
            this.selectedSection = LibrarySection.Pack;
        var scale = ImGuiHelpers.GlobalScale;
        var sections = this.LibrarySections.Where(item => item.Section != LibrarySection.Developer || this.DevToolsEnabled).ToArray();
        float width;
        using (this.ui.NavigationFont.Push())
            width = Math.Max(180f * scale, sections.Max(item => ImGui.CalcTextSize(item.Label).X) + 52f * scale);
        width = Math.Min(width, ImGui.GetContentRegionAvail().X * 0.32f);
        var contentX = ImGui.GetCursorScreenPos().X + MathF.Floor(width) + 2 * ImGui.GetStyle().CellPadding.X;
        this.DrawLibraryBrand(contentX);
        if (this.libraryCollapsed) return;
        ImGui.Separator();
        using var columns = ImRaii.Table("##libraryLayout", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings);
        if (!columns) return;
        ImGui.TableSetupColumn("##navigation", ImGuiTableColumnFlags.WidthFixed, width);
        ImGui.TableSetupColumn("##content", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.ChildBg, ThemeInk))
        using (var sidebar = ImRaii.Child("##libraryNavigation", new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            if (sidebar)
            {
                foreach (var item in sections) this.DrawNavigationItem(item.Section, item.Icon, item.Label);
                var footer = 56f * scale;
                if (ImGui.GetContentRegionAvail().Y > footer)
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ImGui.GetContentRegionAvail().Y - footer);
                this.DrawProjectLink();
            }
        }
        ImGui.TableNextColumn();
        if (this.selectedSection != LibrarySection.Developer) this.RestorePreviewBackground();
        this.IsIssueReportingVisible = this.selectedSection == LibrarySection.Report;
        var hideScrollbar = this.IsIssueReportingVisible || !this.sectionNeedsScroll;
        if (hideScrollbar)
        {
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Vector4.Zero);
        }
        var flags = this.IsIssueReportingVisible
            ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            : this.selectedSection == LibrarySection.Localization ? ImGuiWindowFlags.None
            : ImGuiWindowFlags.AlwaysVerticalScrollbar;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ThemeColor(0x0a0e18));
        using var content = ImRaii.Child("##libraryContent" + this.selectedSection, new Vector2(0, ImGui.GetContentRegionAvail().Y), false, flags);
        ImGui.PopStyleColor();
        if (hideScrollbar) ImGui.PopStyleColor(4);
        if (!content) return;
        this.sectionNeedsScroll = ImGui.GetScrollMaxY() > 0;
        ImGui.Spacing();
        switch (this.selectedSection)
        {
            case LibrarySection.Pack: this.DrawSetupTab(pages, ref changed); break;
            case LibrarySection.Localization: this.DrawPartsTab(ref changed); break;
            case LibrarySection.Report: this.DrawIssueReportingTab(ref changed); break;
            case LibrarySection.Help: this.DrawHelpTab(); break;
            case LibrarySection.Developer: this.DrawDevTab(); break;
        }
        this.DrawPackChangeDialog(ref changed);
    }

    private void DrawNavigationItem(LibrarySection section, FontAwesomeIcon icon, string label)
    {
        using var navigationFont = this.ui.NavigationFont.Push();
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textWidth = Math.Max(1, width - 44f * scale);
        var textSize = ImGui.CalcTextSize(label, false, textWidth);
        var height = Math.Max(44f * scale, textSize.Y + 16f * scale);
        var active = this.selectedSection == section;
        if (PluginUi.Selectable("##section" + section, active, new Vector2(width, height)))
        {
            this.selectedSection = section;
            this.sectionNeedsScroll = false;
        }
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(active ? ThemeGold : Grey);
        if (active) draw.AddRectFilled(start, start + new Vector2(2f * scale, height), color);
        if (section == LibrarySection.Report)
            DrawReportNavigationIcon(start + new Vector2(10f * scale, (height - 16f * scale) / 2), color, 16f * scale);
        else
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                draw.AddText(start + new Vector2(10f * scale, (height - ImGui.GetFontSize()) / 2), color, icon.ToIconString());
        }
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), start + new Vector2(36f * scale, (height - textSize.Y) / 2), color, label, textWidth);
    }

    private static void DrawReportNavigationIcon(Vector2 origin, uint color, float size)
    {
        var draw = ImGui.GetWindowDrawList();
        var arm = size * 0.25f;
        var thickness = 1.5f * ImGuiHelpers.GlobalScale;
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        {
            var corner = origin + new Vector2(x * size, y * size);
            draw.AddLine(corner, corner + new Vector2((1 - 2 * x) * arm, 0), color, thickness);
            draw.AddLine(corner, corner + new Vector2(0, (1 - 2 * y) * arm), color, thickness);
        }
        draw.AddLine(origin + new Vector2(arm, size / 2), origin + new Vector2(size - arm, size / 2), color, thickness);
    }

    private void DrawProjectLink()
    {
        using var font = this.ui.NavigationFont.Push();
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var label = Loc.Localize("Library.ProjectSite", "Visit the project");
        var textWidth = Math.Max(1, width - 44f * scale);
        var textSize = ImGui.CalcTextSize(label, false, textWidth);
        var height = Math.Max(44f * scale, textSize.Y + 16f * scale);
        if (PluginUi.Selectable("##projectSite", false, new Vector2(width, height)))
            Util.OpenLink("https://eorzealocalized.com/");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            SetTooltip("https://eorzealocalized.com/");
        }
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(Blue);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            draw.AddText(start + new Vector2(10f * scale, (height - ImGui.GetFontSize()) / 2), color,
                FontAwesomeIcon.ExternalLinkAlt.ToIconString());
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), start + new Vector2(36f * scale, (height - textSize.Y) / 2),
            color, label, textWidth);
    }

    private void DrawLanguageContact()
    {
        var label = Loc.Localize("Library.AddLanguage", "Want to add another language?");
        const string email = KnownPacks.ProjectContactEmail;
        var width = ImGui.CalcTextSize(label).X + ImGui.CalcTextSize(email).X + ImGui.GetStyle().ItemSpacing.X * 2;
        using (ImRaii.PushFont(UiBuilder.IconFont)) width += ImGui.CalcTextSize(FontAwesomeIcon.Envelope.ToIconString()).X;
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (right - ImGui.GetItemRectMax().X >= width + ImGui.GetStyle().ItemSpacing.X)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - width));
        }
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
            DrawEmailContact(email, true);
        }
    }

    private void DrawPageHeading(string title, string description, Action? trailing = null)
    {
        this.ui.DrawHeading(title);
        trailing?.Invoke();
        using (ImRaii.PushColor(ImGuiCol.Text, Grey)) ImGui.TextWrapped(description);
        ImGui.Spacing();
        ImGui.Spacing();
    }
}
