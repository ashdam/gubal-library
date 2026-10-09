using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private CompatibilitySelection? compatibilityNoticeSelection;
    private Dictionary<string, int> compatibilityNoticeParts = [];

    private void DrawCompatibilityNotice(PageStatus pages)
    {
        if (pages.Compatibility is not { } compatibility || pages.Manifest is not { } manifest) return;
        var running = this.RunningGame();
        var updated = string.CompareOrdinal(running, manifest.GameVersion) > 0;
        var published = pages.Update.Published?.GameVersion;
        var notice = !updated
            ? Loc.Localize("Compatibility.Mode", "This pack was built for a different game version. Translation is running in compatibility mode.")
            : !string.IsNullOrWhiteSpace(published)
                ? string.Equals(published, running, StringComparison.Ordinal)
                    ? Loc.Localize("Compatibility.UpdateAvailable", "The game has been updated, your language pack is in {0} until you update the pack.")
                    : Loc.Localize("Compatibility.UpdatePending", "The game has been updated, but no language pack for this game version is published yet. Translation is running in compatibility mode.")
                : pages.Update.State == UpdateState.Checking
                    ? Loc.Localize("Compatibility.CheckingUpdate", "The game has been updated. Translation is in compatibility mode while we check for a compatible language pack.")
                    : Loc.Localize("Compatibility.UpdateUnknown", "The game has been updated. Translation is in compatibility mode, but we could not check whether a compatible language pack is available.");
        this.DrawCompatibilityText(notice);

        if (!ReferenceEquals(this.compatibilityNoticeSelection, compatibility))
        {
            this.compatibilityNoticeParts = compatibility.Requested
                .Where(path => !path.StartsWith(PackContents.FontPrefix, StringComparison.OrdinalIgnoreCase))
                .GroupBy(ResourceSheet, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.Count(compatibility.Excluded.ContainsKey), StringComparer.OrdinalIgnoreCase);
            this.compatibilityNoticeSelection = compatibility;
        }
        if (this.compatibilityNoticeParts.Values.All(excluded => excluded == 0))
        {
            ImGui.TextWrapped(Loc.Localize("Compatibility.NothingDisabled", "Nothing has been disabled. All enabled parts remain translated."));
        }
        else
        {
            ImGui.TextWrapped(Loc.Localize("Compatibility.AffectedParts", "These parts now use the game's original text or images:"));
            foreach (var group in PackParts.Groups)
            {
                foreach (var part in group.Parts)
                {
                    var excluded = part.Sheets.Sum(sheet => this.compatibilityNoticeParts.GetValueOrDefault(sheet));
                    if (excluded == 0) continue;
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextWrapped($"{group.Name} > {part.Name}");
                }
            }
            var other = this.compatibilityNoticeParts.Where(part => part.Key != "ui/" && !PackParts.IsKnown(part.Key))
                .Select(part => part.Value).ToArray();
            var otherExcluded = other.Sum();
            if (otherExcluded > 0)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(PackParts.OtherGroupName);
            }
            var visuals = this.compatibilityNoticeParts.GetValueOrDefault("ui/");
            if (visuals > 0)
            {
                var name = Loc.Localize("Compatibility.InterfaceAssets", "Interface images and layout");
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(name);
            }
        }
        if (this.isDev && compatibility.Excluded.Count > 0 && ImGui.TreeNode(Loc.Localize("Compatibility.Details", "Technical details")))
        {
            foreach (var path in compatibility.Requested.Where(compatibility.Excluded.ContainsKey).Order(StringComparer.Ordinal))
                ImGui.TextWrapped($"{path}: {compatibility.Excluded[path]}");
            ImGui.TreePop();
        }
        if (updated) ImGui.Separator();
    }

    private void DrawCompatibilityText(string notice)
    {
        var segments = notice.Split("{0}");
        if (segments.Length != 2)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Blue)) ImGui.TextWrapped(notice);
            return;
        }

        var mode = Loc.Localize("Compatibility.ModeLabel", "compatibility mode");
        var width = ImGui.GetContentRegionAvail().X;
        var space = ImGui.CalcTextSize(" ").X;
        var lineWidth = 0f;
        foreach (var (text, color) in new[] { (segments[0], Blue), (mode, Amber), (segments[1], Blue) })
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = ImGui.CalcTextSize(word).X;
                if (lineWidth > 0 && lineWidth + space + wordWidth <= width)
                {
                    ImGui.SameLine(0, space);
                    lineWidth += space;
                }
                else lineWidth = 0;
                ImGui.TextColored(color, word);
                lineWidth += wordWidth;
            }
        }
    }

    private static string ResourceSheet(string path) => path.StartsWith("exd/", StringComparison.OrdinalIgnoreCase)
        ? PackParts.SheetOf(path) : "ui/";
}
