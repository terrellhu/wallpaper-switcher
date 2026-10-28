using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WallpaperSwitcher;

internal static class CollageRenderer
{
    private sealed record Metadata(long Length, DateTime Modified, Size Size);
    private static readonly ConcurrentDictionary<string, Metadata> Sizes = new(StringComparer.OrdinalIgnoreCase);

    public static Size ReadSize(string path)
    {
        var info = new FileInfo(path);
        if (Sizes.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached.Size;
        using var image = Image.FromFile(path);
        var size = image.Size;
        int orientation = Orientation(image);
        if (orientation is >= 5 and <= 8) size = new Size(size.Height, size.Width);
        // Cache dimensions, never decoded source images; bound metadata for large libraries.
        if (Sizes.Count >= 4096) Sizes.Clear();
        Sizes[path] = new(info.Length, info.LastWriteTimeUtc, size);
        return size;
    }

    private static int Orientation(Image image)
    {
        try
        {
            var bytes = image.GetPropertyItem(0x0112)?.Value;
            return bytes is { Length: >= 2 } ? BitConverter.ToUInt16(bytes, 0) : 1;
        }
        catch (ArgumentException) { return 1; }
    }

    public static Image LoadImage(string path)
    {
        var image = Image.FromFile(path);
        var transform = Orientation(image) switch
        {
            2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
        };
        try { if (transform != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(transform); return image; }
        catch { image.Dispose(); throw; }
    }

    public static IReadOnlyList<string> PlanFrame(IReadOnlyList<string> candidates, Size screen, bool enabled, int requestedCount = 0)
    {
        if (candidates.Count == 0) return [];
        if (!enabled) return [candidates[0]];
        requestedCount = CollagePlanner.NormalizeCount(requestedCount);
        var aspects = new List<double>();
        foreach (string path in candidates.Take(requestedCount == 0 ? CollagePlanner.MaxPictures : requestedCount))
        {
            try
            {
                Size size = ReadSize(path);
                double ratio = (double)size.Width / size.Height;
                if (ratio >= 1) break; // Don't reorder or consume the following landscape picture.
                aspects.Add(ratio);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { break; }
        }
        int count = aspects.Count == 0 ? 1 : CollagePlanner.ChooseCount(aspects, (double)screen.Width / screen.Height, requestedCount);
        return candidates.Take(count).ToArray();
    }

    public static Bitmap Render(IReadOnlyList<string> paths, Size screen, int maxEdge = 8192)
    {
        if (paths.Count is < 2 or > CollagePlanner.MaxPictures) throw new ArgumentException("拼贴需要 2 至 6 张图片。");
        double scale = Math.Min(1, (double)maxEdge / Math.Max(screen.Width, screen.Height));
        scale = Math.Min(scale, Math.Sqrt(16777216.0 / ((double)screen.Width * screen.Height)));
        int width = Math.Max(paths.Count, (int)Math.Round(screen.Width * scale));
        int height = Math.Max(1, (int)Math.Round(screen.Height * scale));
        var aspects = paths.Select(path => { var s = ReadSize(path); return (double)s.Width / s.Height; }).ToArray();
        double total = aspects.Sum(), cumulative = 0;
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            int left = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                cumulative += aspects[i];
                int right = i == paths.Count - 1 ? width : Math.Clamp(
                    (int)Math.Round(width * cumulative / total), left + 1, width - (paths.Count - i - 1));
                var cell = new Rectangle(left, 0, right - left, height);
                using var image = LoadImage(paths[i]);
                double zoom = Math.Max((double)cell.Width / image.Width, (double)cell.Height / image.Height);
                float sw = (float)(cell.Width / zoom), sh = (float)(cell.Height / zoom);
                graphics.DrawImage(image, cell, (image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh, GraphicsUnit.Pixel, attributes);
                left = right;
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
