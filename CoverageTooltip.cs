using System.Globalization;
using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace GubalLibrary;

internal sealed partial class ConfigWindow
{
    private static readonly (string Name, string Art)[] ExpansionArt =
    [
        ("A Realm Reborn", "arr"), ("Heavensward", "hw"), ("Stormblood", "sb"),
        ("Shadowbringers", "shb"), ("Endwalker", "ew"), ("Dawntrail", "dt"),
    ];

    private void DrawCoverageTooltip(PackCoverage coverage)
    {
        if (!ImGui.IsItemHovered()) return;

        var scale = ImGuiHelpers.GlobalScale;
        var gold = new Vector4(0.847f, 0.706f, 0.361f, 1);
        using var background = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.025f, 0.035f, 0.06f, 0.99f));
        using var border = ImRaii.PushColor(ImGuiCol.Border, gold with { W = 0.6f });
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 10f * scale);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale));
        ImGui.BeginTooltip();
        var width = Math.Min(660f * scale, ImGui.GetMainViewport().WorkSize.X - 56f * scale);
        var title = Loc.Localize("Coverage.ByExpansion", "Translation by expansion");
        var count = string.Format(CultureInfo.CurrentCulture,
            Loc.Localize("Coverage.Count", "{0:N0} / {1:N0} texts translated"), coverage.Translated, coverage.Total);
        var headerScale = Math.Min(1f, (width - 16f * scale) /
            (ImGui.CalcTextSize(title).X + ImGui.CalcTextSize(count).X));
        var headerStart = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * headerScale, headerStart, ImGui.GetColorU32(gold), title);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * headerScale,
            headerStart + new Vector2(width - ImGui.CalcTextSize(count).X * headerScale, 0),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), count);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight()));
        ImGui.Spacing();

        var expansions = coverage.ByExpansion.ToArray();
        if (expansions.Length == 0)
        {
            ImGui.TextDisabled(Loc.Localize("Coverage.Unavailable", "This pack has no expansion breakdown."));
        }
        else
        {
            var columns = width >= 540f * scale ? 3 : 2;
            var cardSize = new Vector2((width - (columns - 1) * ImGui.GetStyle().ItemSpacing.X) / columns,
                132f * scale);
            // Use explicit card sizes in the auto-sized tooltip.
            for (var i = 0; i < ExpansionArt.Length; i++)
            {
                if (i % columns != 0) ImGui.SameLine();
                var (name, art) = ExpansionArt[i];
                this.DrawExpansionCard(name, art, expansions.FirstOrDefault(e => e.Name == name), gold, cardSize);
            }

            foreach (var expansion in expansions.Where(e => !ExpansionArt.Any(a => a.Name == e.Name)))
            {
                ImGui.Spacing();
                var name = expansion.Name.Length == 0
                    ? Loc.Localize("Coverage.Shared", "Other game text") : expansion.Name;
                ImGui.TextColored(gold, name);
                ImGui.SameLine();
                ImGui.TextUnformatted(expansion.Total > 0 ? CoveragePercent(expansion.Percent)
                    : Loc.Localize("Coverage.NoData", "No data"));
            }
        }

        ImGui.EndTooltip();
    }

    private void DrawExpansionCard(string name, string art, ExpansionCoverage? coverage, Vector4 gold, Vector2 size)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var end = start + size;
        var draw = ImGui.GetWindowDrawList();
        var resource = $"GubalLibrary.images.expansions.exp-{art}.jpg";
        draw.AddRectFilled(start, end, ImGui.GetColorU32(new Vector4(0.06f, 0.08f, 0.12f, 1)), 6f * scale);
        if (this.textures.GetFromManifestResource(typeof(Plugin).Assembly, resource).TryGetWrap(out var image, out _))
        {
            var imageRatio = (float)image.Width / image.Height;
            var cardRatio = size.X / size.Y;
            var visible = imageRatio > cardRatio ? new Vector2(cardRatio / imageRatio, 1)
                : new Vector2(1, imageRatio / cardRatio);
            draw.AddImage(image.Handle, start, end, (Vector2.One - visible) / 2, (Vector2.One + visible) / 2);
        }

        var shadeTop = ImGui.GetColorU32(new Vector4(0.015f, 0.025f, 0.045f, 0.4f));
        var shadeBottom = ImGui.GetColorU32(new Vector4(0.015f, 0.025f, 0.045f, 0.98f));
        draw.AddRectFilledMultiColor(start, end, shadeTop, shadeTop, shadeBottom, shadeBottom);
        draw.AddRect(start, end, ImGui.GetColorU32(gold with { W = 0.55f }), 6f * scale);
        var inset = 10f * scale;
        var fontSize = Math.Min(ImGui.GetFontSize(), (size.X - inset * 2) / ImGui.CalcTextSize(name).X * ImGui.GetFontSize());
        draw.AddText(ImGui.GetFont(), fontSize, start + new Vector2(inset, inset),
            ImGui.GetColorU32(Vector4.One), name);
        var label = coverage is { Total: > 0 } ? CoveragePercent(coverage.Percent)
            : Loc.Localize("Coverage.NoData", "No data");
        var count = coverage is { Total: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} / {1:N0}", coverage.Translated, coverage.Total)
            : string.Empty;
        var rowScale = Math.Min(1f, (size.X - inset * 2 - 8f * scale) /
            (ImGui.CalcTextSize(label).X * 1.5f + ImGui.CalcTextSize(count).X));
        var countFontSize = ImGui.GetFontSize() * rowScale;
        var percentFontSize = countFontSize * 1.5f;
        var rowY = size.Y - 22f * scale - percentFontSize;
        draw.AddText(ImGui.GetFont(), percentFontSize,
            start + new Vector2(inset, rowY), ImGui.GetColorU32(gold), label);
        if (coverage is { Total: > 0 })
        {
            draw.AddText(ImGui.GetFont(), countFontSize,
                start + new Vector2(size.X - inset - ImGui.CalcTextSize(count).X * rowScale,
                    rowY + percentFontSize - countFontSize), ImGui.GetColorU32(Vector4.One), count);
            var bar = start + new Vector2(inset, size.Y - 12f * scale);
            var barSize = new Vector2(size.X - inset * 2, 3f * scale);
            draw.AddRectFilled(bar, bar + barSize, ImGui.GetColorU32(gold with { W = 0.2f }));
            if (coverage.Percent > 0)
                draw.AddRectFilled(bar, bar + barSize * new Vector2((float)(coverage.Percent / 100), 1), ImGui.GetColorU32(gold));
        }

        ImGui.Dummy(size);
    }

    private static string CoveragePercent(double percent) => percent is > 0 and < 1
        ? "<1 %" : percent.ToString("0.#", CultureInfo.CurrentCulture) + " %";
}
