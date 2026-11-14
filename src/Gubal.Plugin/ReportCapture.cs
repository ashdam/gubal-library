using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace GubalLibrary;

internal enum ReportCaptureError { Dimensions, Bytes }
internal sealed class ReportCaptureException(ReportCaptureError error) : Exception
{
    public ReportCaptureError Error { get; } = error;
}
internal sealed record ReportCaptureResult(IDalamudTextureWrap Texture, byte[] Png);

internal sealed class ReportCapture(ITextureProvider textures, ITextureReadbackProvider readback, CancellationToken cancel)
{
    public static Vector2 Size(IDalamudTextureWrap source, Vector2 start, Vector2 end) =>
        new(MathF.Ceiling((end.X - start.X) * source.Width), MathF.Ceiling((end.Y - start.Y) * source.Height));

    public async Task<ReportCaptureResult> CropAsync(IDalamudTextureWrap source, Vector2 start, Vector2 end)
    {
        var size = Size(source, start, end);
        if (size.X > IssueReport.MaxImageDimension || size.Y > IssueReport.MaxImageDimension)
            throw new ReportCaptureException(ReportCaptureError.Dimensions);
        using var cropped = await textures.CreateFromExistingTextureAsync(source, new TextureModificationArgs
        { Uv0 = start, Uv1 = end, NewWidth = (int)size.X, NewHeight = (int)size.Y, MakeOpaque = true, DxgiFormat = 28 },
            true, "Gubal report crop", cancel).ConfigureAwait(false);
        if (cropped.Width > IssueReport.MaxImageDimension || cropped.Height > IssueReport.MaxImageDimension)
            throw new ReportCaptureException(ReportCaptureError.Dimensions);
        return await this.EncodeAsync(cropped).ConfigureAwait(false);
    }

    private async Task<ReportCaptureResult> EncodeAsync(IDalamudTextureWrap texture)
    {
        using var stream = new MemoryStream();
        await readback.SaveToStreamAsync(texture, new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf"), stream,
            leaveWrapOpen: true, leaveStreamOpen: true, cancellationToken: cancel).ConfigureAwait(false);
        if (stream.Length > IssueReport.MaxImageBytes) throw new ReportCaptureException(ReportCaptureError.Bytes);
        var png = stream.ToArray();
        var copy = await textures.CreateFromImageAsync(png, "Gubal report preview", cancel).ConfigureAwait(false);
        return new(copy, png);
    }

    public async Task<ReportCaptureResult> RestoreAsync(byte[] png) =>
        new(await textures.CreateFromImageAsync(png, "Gubal uncensored capture", cancel).ConfigureAwait(false), png);

    public async Task<ReportCaptureResult> RedactAsync(IDalamudTextureWrap source, Vector2 start, Vector2 end)
    {
        var (spec, bytes) = await readback.GetRawImageAsync(source,
            new TextureModificationArgs { DxgiFormat = 28, MakeOpaque = true }, true, cancel).ConfigureAwait(false);
        var left = Math.Clamp((int)(start.X * spec.Width), 0, spec.Width);
        var right = Math.Clamp((int)Math.Ceiling(end.X * spec.Width), 0, spec.Width);
        var top = Math.Clamp((int)(start.Y * spec.Height), 0, spec.Height);
        var bottom = Math.Clamp((int)Math.Ceiling(end.Y * spec.Height), 0, spec.Height);
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var offset = y * spec.Pitch + x * 4;
                bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = 0;
                bytes[offset + 3] = 255;
            }
        using var masked = await textures.CreateFromRawAsync(spec, bytes, "Gubal report redaction", cancel).ConfigureAwait(false);
        return await this.EncodeAsync(masked).ConfigureAwait(false);
    }

}
