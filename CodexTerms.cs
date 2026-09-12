using System.Globalization;
using System.Text;
using Lumina.Text;
using Lumina.Text.ReadOnly;
using Lumina.Text.Payloads;

namespace GubalLibrary;

internal sealed record CodexTerm(uint Id, string Name);

internal sealed class CodexTerms(IEnumerable<CodexTerm> terms)
{
    private readonly CodexTerm[] terms = terms
        .Where(term => term.Name.Length > 1)
        .OrderByDescending(term => term.Name.Length)
        .ThenBy(term => term.Id)
        .ToArray();

    public byte[] Decorate(ReadOnlySpan<byte> source, out int matches)
    {
        matches = 0;
        var builder = new SeStringBuilder();
        var linked = false;
        byte[]? foreground = null;
        foreach (var payload in new ReadOnlySeStringSpan(source))
        {
            if (payload.Type != ReadOnlySePayloadType.Text)
            {
                if (payload.MacroCode == MacroCode.Link)
                {
                    linked = !payload.TryGetExpression(out var expression)
                        || !expression.TryGetUInt(out var type)
                        || type != (uint)LinkMacroPayloadType.Terminator;
                }

                if (payload.MacroCode == MacroCode.ColorType)
                    foreground = new SeStringBuilder().Append(payload).GetViewAsSpan().ToArray();

                builder.Append(payload);
                continue;
            }

            if (linked)
            {
                builder.Append(payload);
                continue;
            }

            var text = Encoding.UTF8.GetString(payload.Body);
            var start = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var match = this.terms.FirstOrDefault(term =>
                    index + term.Name.Length <= text.Length
                    && text.AsSpan(index, term.Name.Length).Equals(term.Name, StringComparison.OrdinalIgnoreCase)
                    && (index == 0 || !IsWord(text[index - 1]))
                    && (index + term.Name.Length == text.Length || !IsWord(text[index + term.Name.Length])));
                if (match is null)
                    continue;

                builder.Append(text.AsSpan(start, index - start));
                builder.PushLinkAkatsukiNote(match.Id).PushColorType(539);
                builder.Append(text.AsSpan(index, match.Name.Length)).PopLink();
                if (foreground is null)
                    builder.PopColorType();
                else
                    builder.Append(new ReadOnlySeStringSpan(foreground));

                matches++;
                index += match.Name.Length - 1;
                start = index + 1;
            }

            builder.Append(text.AsSpan(start));
        }

        return matches == 0 ? source.ToArray() : builder.GetViewAsSpan().ToArray();
    }

    private static bool IsWord(char value) => char.IsLetterOrDigit(value)
        || value is '_' or '\'' or '’'
        || char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
