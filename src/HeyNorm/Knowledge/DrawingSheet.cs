using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using UglyToad.PdfPig;

namespace HeyNorm.Knowledge;

/// <summary>One crop of a scanned sheet, from pass "A" or "B".</summary>
public sealed record SheetTile(string Pass, Rectangle Area)
{
    /// <summary>Stable across runs, so a half-read sheet resumes where it stopped.</summary>
    public string Key => $"{Pass}:{Area.X},{Area.Y},{Area.Width},{Area.Height}";
}

/// <summary>
/// Cuts a scanned drawing into pieces a vision model can read. Measured on a 300 dpi A1 scan
/// (about 9900×7000 px): read whole, the lettering is too small — four-figure dimensions came
/// back with a wrong digit or a dropped one. A 4×3 grid of tiles, each sent at 1600 px on its longest side, reads the large figures
/// correctly.
///
/// Every sheet is read twice with different crops — pass B's grid is shifted half a tile — so
/// each figure is seen in two different contexts. A misread rarely repeats in a different crop:
/// all three misreads of small figures on the test sheet appeared in one pass only.
/// </summary>
public static class DrawingSheet
{
    private const int TargetTileWidth = 2500, TargetTileHeight = 2350;
    internal const int MaxSide = 1600;

    /// <summary>The page's scan: its largest embedded image. Null for a page without one.</summary>
    public static Bitmap? LoadScan(string pdfPath, int page)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var image = pdf.GetPage(page).GetImages().MaxBy(i => (long)i.WidthInSamples * i.HeightInSamples);
        if (image is null) return null;

        // Scans are usually stored as JPEG, which decodes straight from the raw bytes; anything
        // else goes through PdfPig's PNG conversion.
        foreach (var bytes in Candidates(image))
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var decoded = new Bitmap(ms);
                return new Bitmap(decoded); // detached from the stream, which closes here
            }
            catch (ArgumentException) { /* not a format GDI+ reads; try the next */ }
        }
        return null;

        static IEnumerable<byte[]> Candidates(UglyToad.PdfPig.Content.IPdfImage image)
        {
            yield return image.RawMemory.ToArray();
            if (image.TryGetPng(out var png)) yield return png;
        }
    }

    /// <summary>
    /// Pass A then pass B. A big sheet gets a grid in each pass, B's shifted half a tile; a
    /// sheet small enough to read whole gets two whole reads at different sizes instead, since
    /// two identical requests would just agree with themselves.
    /// </summary>
    public static IReadOnlyList<SheetTile> Layout(int width, int height)
    {
        var cols = Math.Max(1, (int)Math.Round((double)width / TargetTileWidth));
        var rows = Math.Max(1, (int)Math.Round((double)height / TargetTileHeight));
        var whole = new Rectangle(0, 0, width, height);

        if (cols == 1 && rows == 1)
            return [new SheetTile("A", whole), new SheetTile("B", whole)];

        var tw = width / cols;
        var th = height / rows;
        var ox = tw / 8;
        var oy = th / 8;
        var tiles = new List<SheetTile>();

        void Add(string pass, int x, int y)
        {
            var area = Rectangle.Intersect(new Rectangle(x - ox, y - oy, tw + 2 * ox, th + 2 * oy), whole);
            // Slivers at the edge of the shifted grid add requests, not readings.
            if (area.Width >= tw / 3 && area.Height >= th / 3) tiles.Add(new SheetTile(pass, area));
        }

        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                Add("A", c * tw, r * th);
        for (var r = 0; r <= rows; r++)
            for (var c = 0; c <= cols; c++)
                Add("B", c * tw - tw / 2, r * th - th / 2);
        return tiles;
    }

    /// <summary>
    /// The tile as a JPEG at most 1600 px on its longest side — by the longest side, not the
    /// width: measured, a tall edge tile sent at full height overflowed the model's context and
    /// Ollama answered 400. A whole small sheet's second read goes at three-quarter size.
    /// </summary>
    public static byte[] Crop(Bitmap sheet, SheetTile tile)
    {
        var area = tile.Area;
        var limit = tile.Pass == "B" && area.Size == sheet.Size ? MaxSide * 3 / 4 : MaxSide;
        var scale = Math.Min(1.0, (double)limit / Math.Max(area.Width, area.Height));
        using var crop = new Bitmap(Math.Max(1, (int)(area.Width * scale)), Math.Max(1, (int)(area.Height * scale)));
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(sheet, new Rectangle(Point.Empty, crop.Size), area, GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 88L) } };
        crop.Save(ms, jpeg, quality);
        return ms.ToArray();
    }

    /// <summary>Where a tile sits, in words someone can find on the paper: "top left", "middle", "bottom right".</summary>
    public static string Where(Rectangle area, int width, int height)
    {
        var cx = area.X + area.Width / 2.0;
        var cy = area.Y + area.Height / 2.0;
        var row = cy < height / 3.0 ? "top" : cy < height * 2 / 3.0 ? "middle" : "bottom";
        var col = cx < width / 3.0 ? "left" : cx < width * 2 / 3.0 ? "middle" : "right";
        return (row, col) switch
        {
            ("middle", "middle") => "middle",
            (_, "middle") => $"{row} middle",
            ("middle", _) => $"middle {col}",
            _ => $"{row} {col}",
        };
    }
}
