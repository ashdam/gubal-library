using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private void DrawInGameFeatures(ref bool changed)
    {
        var namesLabel = Loc.Localize("Library.EnglishTooltipNames", "Show English names in tooltips:");
        var itemLabel = Loc.Localize("Items.ShowEnglishNames", "Items");
        var actionLabel = Loc.Localize("Actions.ShowEnglishNames", "Abilities");
        var traitLabel = Loc.Localize("Traits.ShowEnglishNames", "Traits");
        var codexLabel = Loc.Localize("Codex.Enable", "Enable Unending Codex links on dialog");
        var codexHint = Loc.Localize("Codex.Preview", "Open Unending Codex entries from highlighted dialogue terms.");
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().WindowPadding.X * 2);
        var rowIndent = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var labelWidth = Math.Max(1, width - rowIndent - ImGui.GetFrameHeightWithSpacing());
        var nameOptionsWidth = EnglishNameOptionWidth(itemLabel) + EnglishNameOptionWidth(actionLabel)
            + EnglishNameOptionWidth(traitLabel);
        var namesInline = ImGui.CalcTextSize(namesLabel).X + ImGui.GetStyle().ItemSpacing.X + nameOptionsWidth <= width;
        var title = Loc.Localize("Library.Features", "In-game features");
        var restart = Loc.Localize("Library.NoRestart", "No restart required");
        var inline = PluginUi.SubheadingSize(title).X + ImGui.CalcTextSize(restart).X + ImGui.GetStyle().ItemSpacing.X <= width;
        var height = PluginUi.SubheadingSize(title).Y + ImGui.GetStyle().ItemSpacing.Y
            + (inline ? 0 : ImGui.GetTextLineHeightWithSpacing())
            + ImGui.GetFrameHeight() + (namesInline ? 0 : ImGui.GetTextLineHeightWithSpacing())
            + Math.Max(ImGui.GetFrameHeight(), ImGui.CalcTextSize(codexLabel, false, labelWidth).Y)
            + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetStyle().ItemSpacing.Y * 5 + 1;
        using var options = ImRaii.Child("##localizationOptions", new Vector2(0, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!options) return;
        PluginUi.DrawSubheading(title, restart, Green);
        ImGui.Separator();
        var row = ImGui.GetCursorScreenPos();
        var contentX = row.X;
        var ink = PluginUi.TextInkBounds(namesLabel);
        var labelY = namesInline ? row.Y + (ImGui.GetFrameHeight() - ink.Bottom + ink.Top) / 2 : row.Y;
        ImGui.GetWindowDrawList().AddText(new Vector2(row.X, labelY - ink.Top), ImGui.GetColorU32(ImGuiCol.Text), namesLabel);
        if (namesInline) row.X += ImGui.CalcTextSize(namesLabel).X + ImGui.GetStyle().ItemSpacing.X;
        else row.Y += ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorScreenPos(row);
        var items = this.config.ShowEnglishItemNames;
        if (this.DrawFeatureOption("englishNames", itemLabel,
                Loc.Localize("Library.EnglishNamesHint", "Keep the original item name visible alongside the translation."),
                "item-english-tooltips", ref items, labelColor: PluginTheme.Gold))
        {
            this.config.ShowEnglishItemNames = items;
            changed = true;
        }
        row.X += EnglishNameOptionWidth(itemLabel);
        ImGui.SetCursorScreenPos(row);
        var actions = this.config.ShowEnglishActionNames;
        if (this.DrawFeatureOption("englishActionNames", actionLabel,
                Loc.Localize("Actions.EnglishNamesHint", "Show the original action name below the translated name."),
                null, ref actions, labelColor: PluginTheme.Gold))
        {
            this.config.ShowEnglishActionNames = actions;
            changed = true;
        }
        row.X += EnglishNameOptionWidth(actionLabel);
        ImGui.SetCursorScreenPos(row);
        var traits = this.config.ShowEnglishTraitNames;
        if (this.DrawFeatureOption("englishTraitNames", traitLabel,
                Loc.Localize("Traits.EnglishNamesHint", "Show the original trait name below the translated name."),
                null, ref traits, labelColor: PluginTheme.Gold))
        {
            this.config.ShowEnglishTraitNames = traits;
            changed = true;
        }
        ImGui.SetCursorScreenPos(new Vector2(contentX,
            row.Y + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2));
        var codex = this.config.EnableCodexLinks;
        if (this.DrawFeatureOption("codex", codexLabel, codexHint, "codex", ref codex,
                new Vector2(0.295f, 0.777f), new Vector2(0.705f, 0.945f)))
        {
            this.config.EnableCodexLinks = codex;
            changed = true;
        }
    }

    private static float EnglishNameOptionWidth(string label) => ImGui.GetFrameHeight()
        + ImGui.CalcTextSize(label).X + ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.X * 3;

    private bool DrawFeatureOption(string id, string label, string description, string? picture, ref bool enabled,
        Vector2? uv0 = null, Vector2? uv1 = null, Vector4? labelColor = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var controlHeight = ImGui.GetFrameHeight();
        var labelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - controlHeight
            - ImGui.GetStyle().ItemSpacing.X - ImGui.GetFrameHeightWithSpacing());
        var textSize = ImGui.CalcTextSize(label, false, labelWidth);
        var ink = PluginUi.TextInkBounds(label);
        var textHeight = textSize.Y - ImGui.GetTextLineHeight() + ink.Bottom - ink.Top;
        var rowHeight = Math.Max(controlHeight, textHeight);
        ImGui.SetCursorScreenPos(start + new Vector2(0, (rowHeight - controlHeight) / 2));
        var changed = PluginUi.Checkbox("##" + id, ref enabled);
        var centerY = (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2;
        ImGui.SameLine();
        var labelPosition = new Vector2(ImGui.GetCursorScreenPos().X, centerY - textHeight / 2);
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize(), labelPosition - new Vector2(0, ink.Top),
            labelColor.HasValue ? ImGui.GetColorU32(labelColor.Value) : ImGui.GetColorU32(ImGuiCol.Text), label, labelWidth);
        ImGui.SetCursorScreenPos(labelPosition);
        ImGui.Dummy(new Vector2(textSize.X, textHeight));
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            enabled = !enabled;
            changed = true;
        }
        this.DrawFeaturePreview(description, picture, centerY, uv0, uv1);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y * 2));
        return changed;
    }
}
