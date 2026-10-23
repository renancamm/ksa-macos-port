// Screenshots as pixels: a small PNG reader and writer (8-bit RGB and RGBA without interlacing, as KSA Metal writes
// them) and the comparison of two images block by block. Shared by the mod, which waits for the view to stop changing
// before a screenshot in fast mode, and the report program, which compares the screenshots of two runs.
//
// The comparison works on blocks of pixels rather than single pixels: terrain detail, grass and the clouds' temporal
// noise move single pixels a little from run to run, while a real change (a missing texture, a broken shader, a
// different place) changes whole blocks.
using System;
using System.IO;
using System.IO.Compression;

namespace KsaMetalBenchmark;

public sealed class Image(int width, int height, byte[] rgb)
{
    public readonly int Width = width, Height = height;
    // Three bytes a pixel, rows from the top
    public readonly byte[] Rgb = rgb;
}

// How two images differ: the share of their blocks that changed (in percent), the mean change of a block (0 to 255)
// and which blocks changed, row by row
public sealed record Difference(double ChangedPercent, double MeanChange, bool[] Changed, int Columns, int Rows);

public static class Images
{
    // A block is this many pixels square, and changed when one of its mean colours moved by more than the limit
    public const int Block = 16;
    public const double BlockLimit = 12;

    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // The image in a PNG file; null when there is none or it cannot be read (the driver may still be writing it)
    public static Image? Read(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static Image? Decode(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
            return null;
        int width = 0, height = 0, channels = 0;
        var idat = new MemoryStream();
        bool ended = false;
        for (int at = 8; at + 8 <= png.Length;)
        {
            int length = BigEndian(png, at);
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            int data = at + 8;
            if (length < 0 || data + length > png.Length)
                return null;
            if (type == "IHDR")
            {
                width = BigEndian(png, data);
                height = BigEndian(png, data + 4);
                byte depth = png[data + 8], color = png[data + 9], interlace = png[data + 12];
                channels = color switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || channels == 0 || interlace != 0)
                    return null;
            }
            else if (type == "IDAT")
                idat.Write(png, data, length);
            else if (type == "IEND")
            {
                ended = true;
                break;
            }
            at = data + length + 4;
        }
        if (!ended || width <= 0 || height <= 0)
            return null;
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);
        var rgb = new byte[width * height * 3];
        var previous = new byte[stride];
        var row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int start = y * (stride + 1);
            byte filter = raw[start];
            for (int x = 0; x < stride; x++)
            {
                int left = x >= channels ? row[x - channels] : 0, up = previous[x], upLeft = x >= channels ? previous[x - channels] : 0;
                int value = raw[start + 1 + x];
                row[x] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + (left + up) / 2,
                    4 => value + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("unknown PNG filter")
                });
            }
            for (int x = 0; x < width; x++)
            {
                rgb[(y * width + x) * 3] = row[x * channels];
                rgb[(y * width + x) * 3 + 1] = row[x * channels + 1];
                rgb[(y * width + x) * 3 + 2] = row[x * channels + 2];
            }
            (previous, row) = (row, previous);
        }
        return new Image(width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int BigEndian(byte[] b, int at) => b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];

    public static void Write(string path, Image image)
    {
        using var file = File.Create(path);
        file.Write(Signature);
        var header = new byte[13];
        PutBigEndian(header, 0, image.Width);
        PutBigEndian(header, 4, image.Height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        var pixels = new MemoryStream();
        using (var z = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < image.Height; y++)
            {
                z.WriteByte(0);
                z.Write(image.Rgb, y * image.Width * 3, image.Width * 3);
            }
        }
        Chunk(file, "IDAT", pixels.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        PutBigEndian(head, 0, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
        s.Write(head);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, head.AsSpan(4)), data) ^ 0xFFFFFFFF;
        var tail = new byte[4];
        PutBigEndian(tail, 0, (int)crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = MakeCrcTable();

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void PutBigEndian(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    // Block by block: two images of different sizes differ everywhere
    public static Difference Compare(Image a, Image b)
    {
        int columns = (a.Width + Block - 1) / Block, rows = (a.Height + Block - 1) / Block;
        var changed = new bool[columns * rows];
        if (a.Width != b.Width || a.Height != b.Height)
        {
            Array.Fill(changed, true);
            return new Difference(100, 255, changed, columns, rows);
        }
        var ma = Means(a, columns, rows);
        var mb = Means(b, columns, rows);
        int count = 0;
        double total = 0;
        for (int i = 0; i < changed.Length; i++)
        {
            double change = Math.Max(Math.Abs(ma[i * 3] - mb[i * 3]), Math.Max(Math.Abs(ma[i * 3 + 1] - mb[i * 3 + 1]), Math.Abs(ma[i * 3 + 2] - mb[i * 3 + 2])));
            total += change;
            if (change > BlockLimit)
            {
                changed[i] = true;
                count++;
            }
        }
        return new Difference(100.0 * count / changed.Length, total / changed.Length, changed, columns, rows);
    }

    // The mean colour of every block
    private static double[] Means(Image image, int columns, int rows)
    {
        var sums = new double[columns * rows * 3];
        var counts = new int[columns * rows];
        for (int y = 0; y < image.Height; y++)
        {
            int rowAt = y / Block * columns;
            for (int x = 0; x < image.Width; x++)
            {
                int block = rowAt + x / Block, p = (y * image.Width + x) * 3;
                sums[block * 3] += image.Rgb[p];
                sums[block * 3 + 1] += image.Rgb[p + 1];
                sums[block * 3 + 2] += image.Rgb[p + 2];
                counts[block]++;
            }
        }
        for (int i = 0; i < counts.Length; i++)
        {
            for (int c = 0; c < 3; c++)
                sums[i * 3 + c] /= Math.Max(counts[i], 1);
        }
        return sums;
    }

    // The image with the blocks that changed tinted red and the rest dimmed, to see where two screenshots differ
    public static Image Mark(Image image, Difference d)
    {
        var rgb = new byte[image.Rgb.Length];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                int p = (y * image.Width + x) * 3;
                bool changed = d.Changed[y / Block * d.Columns + x / Block];
                byte r = image.Rgb[p], g = image.Rgb[p + 1], b = image.Rgb[p + 2];
                rgb[p] = changed ? (byte)(r / 2 + 128) : (byte)(r / 3);
                rgb[p + 1] = changed ? (byte)(g / 2) : (byte)(g / 3);
                rgb[p + 2] = changed ? (byte)(b / 2) : (byte)(b / 3);
            }
        }
        return new Image(image.Width, image.Height, rgb);
    }
}
