using System.IO.Compression;
using System.Text;

namespace MtrxSys.WahaEmulator;

/// <summary>Gera um PNG que PARECE um QR code (grid preto/branco com os 3 "olhos" nos cantos),
/// derivado de uma semente. Não é um QR válido — é só pra "ver como fica" o onboarding sem WAHA
/// real. PNG escrito à mão (grayscale + IDAT via zlib) pra não depender de libs nativas no Alpine.</summary>
public static class FakeQr
{
    private const int Modules = 25;     // 25x25 módulos, como um QR pequeno
    private const int Scale = 8;        // 8px por módulo -> 200x200
    private const int Quiet = 2;        // borda branca (quiet zone), em módulos

    public static byte[] Png(string seed)
    {
        var grid = BuildGrid(seed);
        var dim = (Modules + Quiet * 2) * Scale;
        var raw = Rasterize(grid, dim);
        return Encode(raw, dim, dim);
    }

    private static bool[,] BuildGrid(string seed)
    {
        var g = new bool[Modules, Modules];
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        for (var y = 0; y < Modules; y++)
        {
            for (var x = 0; x < Modules; x++)
            {
                var bit = bytes[(y * Modules + x) % bytes.Length] >> ((x + y) % 8) & 1;
                g[y, x] = bit == 1;
            }
        }
        StampFinder(g, 0, 0);
        StampFinder(g, 0, Modules - 7);
        StampFinder(g, Modules - 7, 0);
        return g;
    }

    // "Olho" 7x7 do QR (anel preto com miolo preto e folga branca).
    private static void StampFinder(bool[,] g, int top, int left)
    {
        for (var y = 0; y < 7; y++)
        {
            for (var x = 0; x < 7; x++)
            {
                var border = x == 0 || x == 6 || y == 0 || y == 6;
                var core = x >= 2 && x <= 4 && y >= 2 && y <= 4;
                g[top + y, left + x] = border || core;
            }
        }
        // folga branca de 1 módulo ao redor (quando cabe no grid)
        for (var i = -1; i <= 7; i++)
        {
            Clear(g, top - 1, left + i);
            Clear(g, top + 7, left + i);
            Clear(g, top + i, left - 1);
            Clear(g, top + i, left + 7);
        }
    }

    private static void Clear(bool[,] g, int y, int x)
    {
        if (y >= 0 && y < Modules && x >= 0 && x < Modules)
        {
            g[y, x] = false;
        }
    }

    // Cada pixel: 0 = preto, 255 = branco (grayscale 8-bit).
    private static byte[] Rasterize(bool[,] grid, int dim)
    {
        var raw = new byte[(dim + 1) * dim]; // +1 byte de filtro por linha
        for (var py = 0; py < dim; py++)
        {
            var rowStart = py * (dim + 1);
            raw[rowStart] = 0; // filtro "None"
            for (var px = 0; px < dim; px++)
            {
                var mx = px / Scale - Quiet;
                var my = py / Scale - Quiet;
                var dark = mx >= 0 && mx < Modules && my >= 0 && my < Modules && grid[my, mx];
                raw[rowStart + 1 + px] = (byte)(dark ? 0 : 255);
            }
        }
        return raw;
    }

    private static byte[] Encode(byte[] raw, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // assinatura PNG

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 0;   // color type 0 = grayscale
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(ms, "IHDR", ihdr);

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }
        WriteChunk(ms, "IDAT", idat.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBE(crcBytes, 0, crc);
        s.Write(crcBytes);
    }

    private static void WriteBE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint crc = 0xFFFFFFFF;
        crc = Crc32Step(crc, a);
        crc = Crc32Step(crc, b);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Crc32Step(uint crc, byte[] data)
    {
        foreach (var x in data)
        {
            crc ^= x;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }
        return crc;
    }
}
