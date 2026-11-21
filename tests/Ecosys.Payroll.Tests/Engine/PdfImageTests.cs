using System.Buffers.Binary;
using System.IO.Compression;
using Ecosys.Payroll.Infrastructure.Documents;

namespace Ecosys.Payroll.Tests.Engine;

/// <summary>Letterhead images: PNG decoding (every row filter, alpha, palette) and JPEG headers, and embedding in a PDF.</summary>
public class PdfImageTests
{
    /// <summary>Builds a PNG from raw rows, applying the given row filter, so the decoder's unfiltering is exercised.</summary>
    public static byte[] Png(int w, int h, byte colorType, Func<int, int, byte[]> pixel, byte filter = 0, byte[]? palette = null, byte[]? trns = null, byte depth = 8)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var bpp = channels;
        using var raw = new MemoryStream();
        var prev = new byte[w * bpp];
        for (var y = 0; y < h; y++)
        {
            var row = new byte[w * bpp];
            for (var x = 0; x < w; x++) pixel(x, y).CopyTo(row, x * bpp);
            raw.WriteByte(filter);
            for (var i = 0; i < row.Length; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], ul = i >= bpp ? prev[i - bpp] : 0;
                int p = left + up - ul, pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - ul);
                var paeth = pa <= pb && pa <= pc ? left : pb <= pc ? up : ul;
                raw.WriteByte(filter switch { 1 => (byte)(row[i] - left), 2 => (byte)(row[i] - up), 3 => (byte)(row[i] - (left + up) / 2), 4 => (byte)(row[i] - paeth), _ => row[i] });
            }
            prev = row;
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest)) zs.Write(raw.ToArray());
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = depth; ihdr[9] = colorType;
        Chunk(png, "IHDR", ihdr);
        if (palette is not null) Chunk(png, "PLTE", palette);
        if (trns is not null) Chunk(png, "tRNS", trns);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); s.Write(len);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(body);
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body)); s.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFFu;
    }

    private static byte[] Inflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress)) z.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] Rgba(int x, int y) => [(byte)(x * 40), (byte)(y * 50), (byte)(x * 13 + y * 7), (byte)(x == 0 ? 0 : 255)];

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Rgba_png_decodes_with_every_row_filter_and_keeps_transparency(byte filter)
    {
        var img = PdfImage.Load(Png(5, 4, 6, Rgba, filter));
        Assert.Equal((5, 4), (img.Width, img.Height));
        Assert.Equal("DeviceRGB", img.ColorSpace);
        var rgb = Inflate(img.Data);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 5; x++)
                Assert.Equal(Rgba(x, y)[..3], rgb.AsSpan((y * 5 + x) * 3, 3).ToArray());
        var alpha = Inflate(img.SoftMask!.Data);
        Assert.Equal(0, alpha[0]);
        Assert.Equal(255, alpha[1]);
    }

    [Fact]
    public void Opaque_rgb_png_has_no_soft_mask()
    {
        var img = PdfImage.Load(Png(3, 3, 2, (x, y) => [(byte)x, (byte)y, 9], filter: 4));
        Assert.Null(img.SoftMask);
        Assert.Equal(new byte[] { 2, 1, 9 }, Inflate(img.Data).AsSpan((1 * 3 + 2) * 3, 3).ToArray());
    }

    [Fact]
    public void Palette_png_with_transparent_entry_becomes_rgb_and_alpha()
    {
        byte[] palette = [255, 0, 0, 0, 0, 255];
        var img = PdfImage.Load(Png(2, 1, 3, (x, _) => [(byte)x], palette: palette, trns: [0]));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255 }, Inflate(img.Data));
        Assert.Equal(new byte[] { 0, 255 }, Inflate(img.SoftMask!.Data));
    }

    [Fact]
    public void Grayscale_png_stays_gray()
    {
        var img = PdfImage.Load(Png(2, 2, 0, (x, y) => [(byte)(x * 100 + y)]));
        Assert.Equal("DeviceGray", img.ColorSpace);
        Assert.Equal(new byte[] { 0, 100, 1, 101 }, Inflate(img.Data));
    }

    [Fact]
    public void Jpeg_is_embedded_as_is_and_cmyk_is_refused()
    {
        byte[] Jpeg(byte comps) => [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 50, 0, 120, comps, 0, 0, 0, 0xFF, 0xD9];
        var img = PdfImage.Load(Jpeg(3));
        Assert.Equal((120, 50, "DCTDecode", "DeviceRGB"), (img.Width, img.Height, img.Filter, img.ColorSpace));
        Assert.Throws<NotSupportedException>(() => PdfImage.Load(Jpeg(4)));
        Assert.Throws<NotSupportedException>(() => PdfImage.Load("GIF89a........"u8.ToArray()));
    }

    [Fact]
    public void Pdf_with_an_image_references_it_from_the_page()
    {
        var pdf = new PdfWriter();
        var logo = PdfImage.Load(Png(4, 4, 6, Rgba));
        pdf.Image(logo, 40, 40, 40, 40);
        pdf.Text(100, 60, "Acme", 12, color: PdfColor.FromHex("#0b5d4f"));
        var text = System.Text.Encoding.Latin1.GetString(pdf.ToBytes());
        Assert.Contains("/Subtype /Image /Width 4 /Height 4 /ColorSpace /DeviceRGB", text);
        Assert.Contains("/SMask", text);
        Assert.Contains("/XObject << /Im0", text);
        Assert.Contains("/Im0 Do", text);
        Assert.Contains("0.043 0.365 0.31 rg", text);
        Assert.EndsWith("%%EOF\n", text);
    }
}
