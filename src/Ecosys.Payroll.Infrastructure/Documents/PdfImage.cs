using System.Buffers.Binary;
using System.IO.Compression;

namespace Ecosys.Payroll.Infrastructure.Documents;

/// <summary>RGB colour for PDF drawing, from a #RRGGBB string.</summary>
public readonly record struct PdfColor(float R, float G, float B)
{
    public static PdfColor? FromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)) return null;
        return new PdfColor(((v >> 16) & 255) / 255f, ((v >> 8) & 255) / 255f, (v & 255) / 255f);
    }

    /// <summary>Relative luminance (WCAG); above ~0.6 the colour is too pale for text on white.</summary>
    public float Luminance
    {
        get
        {
            static float L(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * L(R) + 0.7152f * L(G) + 0.0722f * L(B);
        }
    }
}

/// <summary>
/// An image ready to embed in a PDF. JPEG is embedded as-is (DCTDecode). PNG is decoded and re-compressed as
/// Flate, with transparency carried in a soft mask, so logos with transparent backgrounds print cleanly.
/// </summary>
public sealed class PdfImage
{
    public const int MaxSide = 4000;

    public int Width { get; private init; }
    public int Height { get; private init; }
    public string ColorSpace { get; private init; } = "DeviceRGB";
    public string Filter { get; private init; } = "FlateDecode";
    public byte[] Data { get; private init; } = [];
    /// <summary>Alpha channel as a DeviceGray Flate image, when the PNG has transparency.</summary>
    public PdfImage? SoftMask { get; private init; }

    public float Aspect => (float)Width / Height;

    /// <summary>Reads a PNG or JPEG. Throws <see cref="NotSupportedException"/> with a message fit to show the user.</summary>
    public static PdfImage Load(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G') return FromPng(bytes);
        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8) return FromJpeg(bytes);
        throw new NotSupportedException("Use a PNG or JPG image.");
    }

    private static PdfImage FromJpeg(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var len = (b[i + 2] << 8) | b[i + 3];
            // Start-of-frame markers carry the size and colour components (C4 = Huffman table, C8 = reserved, CC = arithmetic table).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var h = (b[i + 5] << 8) | b[i + 6];
                var w = (b[i + 7] << 8) | b[i + 8];
                var comps = b[i + 9];
                if (comps == 4) throw new NotSupportedException("This JPG uses CMYK colour (print format). Save it as an RGB JPG or a PNG.");
                Check(w, h);
                return new PdfImage { Width = w, Height = h, ColorSpace = comps == 1 ? "DeviceGray" : "DeviceRGB", Filter = "DCTDecode", Data = b };
            }
            i += 2 + len;
        }
        throw new NotSupportedException("This JPG could not be read. Save it again from an image editor.");
    }

    private static PdfImage FromPng(byte[] b)
    {
        int w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        var p = 8;
        while (p + 8 <= b.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            var kind = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            if (len < 0 || p + 12 + len > b.Length) throw new NotSupportedException("This PNG is damaged. Save it again from an image editor.");
            var data = b.AsSpan(p + 8, len);
            switch (kind)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(data); h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (kind == "IEND") break;
            p += 12 + len;
        }
        Check(w, h);
        if (interlace != 0) throw new NotSupportedException("Interlaced PNGs aren't supported. Save the image again with interlacing turned off.");
        var channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException("This PNG's colour format isn't supported.") };
        var okDepth = type switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => depth is 8 or 16 };
        if (!okDepth || type == 3 && palette is null) throw new NotSupportedException("This PNG's colour format isn't supported.");

        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        var bitsPerPixel = channels * depth;
        var stride = (w * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var src = raw.GetBuffer();
        if (raw.Length < (long)(stride + 1) * h) throw new NotSupportedException("This PNG is damaged. Save it again from an image editor.");

        var gray = type is 0 or 4;
        var colour = new byte[w * h * (gray ? 1 : 3)];
        var alpha = new byte[w * h];
        var anyAlpha = false;
        var prev = new byte[stride];
        var row = new byte[stride];
        for (var y = 0; y < h; y++)
        {
            var off = y * (stride + 1);
            var filter = src[off];
            Array.Copy(src, off + 1, row, 0, stride);
            Unfilter(filter, row, prev, bpp);
            for (var x = 0; x < w; x++)
            {
                var px = y * w + x;
                byte a = 255;
                switch (type)
                {
                    case 0:
                        colour[px] = (byte)Sample(row, x, channels, depth, type, 0);
                        if (depth == 8 && trns is { Length: >= 2 } && colour[px] == trns[1]) a = 0; // colour-key transparency (8-bit only)
                        break;
                    case 4: colour[px] = (byte)Sample(row, x, channels, depth, type, 0); a = (byte)Sample(row, x, channels, depth, type, 1); break;
                    case 2:
                        for (var c = 0; c < 3; c++) colour[px * 3 + c] = (byte)Sample(row, x, channels, depth, type, c);
                        if (trns is { Length: >= 6 } && colour[px * 3] == trns[depth == 16 ? 0 : 1] && colour[px * 3 + 1] == trns[depth == 16 ? 2 : 3] && colour[px * 3 + 2] == trns[depth == 16 ? 4 : 5]) a = 0;
                        break;
                    case 6: for (var c = 0; c < 3; c++) colour[px * 3 + c] = (byte)Sample(row, x, channels, depth, type, c); a = (byte)Sample(row, x, channels, depth, type, 3); break;
                    case 3:
                        var idx = Sample(row, x, channels, depth, type, 0);
                        if (idx * 3 + 2 >= palette!.Length) throw new NotSupportedException("This PNG is damaged. Save it again from an image editor.");
                        colour[px * 3] = palette[idx * 3]; colour[px * 3 + 1] = palette[idx * 3 + 1]; colour[px * 3 + 2] = palette[idx * 3 + 2];
                        if (trns is not null && idx < trns.Length) a = trns[idx];
                        break;
                }
                alpha[px] = a;
                if (a != 255) anyAlpha = true;
            }
            (prev, row) = (row, prev);
        }
        return new PdfImage
        {
            Width = w, Height = h, ColorSpace = gray ? "DeviceGray" : "DeviceRGB", Data = Deflate(colour),
            SoftMask = anyAlpha ? new PdfImage { Width = w, Height = h, ColorSpace = "DeviceGray", Data = Deflate(alpha) } : null,
        };
    }

    /// <summary>One channel of one pixel, as 0–255 (or the raw palette index for palette images).</summary>
    private static int Sample(byte[] row, int x, int channels, int depth, int type, int channel)
    {
        if (depth == 8) return row[x * channels + channel];
        if (depth == 16) return row[(x * channels + channel) * 2];
        var bit = (x * channels + channel) * depth;
        var v = (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        return type == 3 ? v : v * 255 / ((1 << depth) - 1);
    }

    private static void Unfilter(byte filter, byte[] row, byte[] prev, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int left = i >= bpp ? row[i - bpp] : 0, up = prev[i], upLeft = i >= bpp ? prev[i - bpp] : 0;
            row[i] = filter switch
            {
                0 => row[i],
                1 => (byte)(row[i] + left),
                2 => (byte)(row[i] + up),
                3 => (byte)(row[i] + (left + up) / 2),
                4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                _ => throw new NotSupportedException("This PNG is damaged. Save it again from an image editor."),
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(data);
        return ms.ToArray();
    }

    private static void Check(int w, int h)
    {
        if (w <= 0 || h <= 0) throw new NotSupportedException("This image could not be read.");
        if (w > MaxSide || h > MaxSide) throw new NotSupportedException($"The image is {w}×{h} pixels. Use one no larger than {MaxSide} pixels on each side.");
    }
}
