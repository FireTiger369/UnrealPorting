using System;
using System.Drawing;
using System.Drawing.Imaging;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;

namespace UnrealPorting.Helpers
{
    public static class TextureDecoder
    {
        public static Bitmap Decode(UTexture2D tex)
        {
            try
            {
                if (tex == null)
                {
                    Console.WriteLine("[DECODER] tex was null");
                    return SolidColor(128, 128, Color.Magenta);
                }

                Console.WriteLine("[DECODER] Format = " + tex.Format);

                if (tex.PlatformData == null || tex.PlatformData.Mips == null || tex.PlatformData.Mips.Length == 0)
                {
                    Console.WriteLine("[DECODER] Missing PlatformData/Mips");
                    return SolidColor(128, 128, Color.Magenta);
                }

                foreach (var mip in tex.PlatformData.Mips)
                {
                    try
                    {
                        _ = mip?.BulkData?.Data;
                    }
                    catch { }
                }

                var mip0 = tex.PlatformData.Mips[0];
                if (mip0 == null || mip0.BulkData?.Data == null)
                {
                    Console.WriteLine("[DECODER] mip0/BulkData missing");
                    return SolidColor(128, 128, Color.Magenta);
                }

                var data = mip0.BulkData.Data;
                int width = mip0.SizeX;
                int height = mip0.SizeY;

                switch (tex.Format)
                {
                    case EPixelFormat.PF_R8G8B8A8:
                        return FromRawRGBA(data, width, height);

                    case EPixelFormat.PF_B8G8R8A8:
                        return FromRawBGRA(data, width, height);

                    case EPixelFormat.PF_G8:
                        return FromRawG8(data, width, height);

                    case EPixelFormat.PF_DXT1:
                        return FromBC1(data, width, height);

                    case EPixelFormat.PF_DXT5:
                        return FromBC3(data, width, height);

                    case EPixelFormat.PF_BC5:
                        return FromBC5(data, width, height);

                    case EPixelFormat.PF_BC7:
                        Console.WriteLine("[DECODER] BC7 (placeholder)");
                        return SolidColor(width, height, Color.Magenta);

                    default:
                        Console.WriteLine("[DECODER] Unsupported " + tex.Format);
                        return SolidColor(width, height, Color.DarkSlateBlue);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DECODER ERROR] " + ex);
                return SolidColor(128, 128, Color.Magenta);
            }
        }


        // ------------ RAW FORMATS ------------

        private static Bitmap FromRawRGBA(byte[] data, int width, int height)
        {
            // Data is RGBA; Bitmap wants BGRA. Swap R/B.
            var tmp = (byte[])data.Clone();
            for (int i = 0; i < tmp.Length; i += 4)
            {
                byte r = tmp[i + 0];
                byte g = tmp[i + 1];
                byte b = tmp[i + 2];
                byte a = tmp[i + 3];

                tmp[i + 0] = b;
                tmp[i + 1] = g;
                tmp[i + 2] = r;
                tmp[i + 3] = a;
            }

            return BytesToBitmap(tmp, width, height);
        }

        private static Bitmap FromRawBGRA(byte[] data, int width, int height)
        {
            // Already BGRA, can go straight into bitmap
            return BytesToBitmap(data, width, height);
        }

        private static Bitmap FromRawG8(byte[] data, int width, int height)
        {
            // Single-channel grayscale → RGBA
            var outBytes = new byte[width * height * 4];

            for (int i = 0; i < width * height; i++)
            {
                byte g = i < data.Length ? data[i] : (byte)0;
                int o = i * 4;
                outBytes[o + 0] = g;   // B
                outBytes[o + 1] = g;   // G
                outBytes[o + 2] = g;   // R
                outBytes[o + 3] = 255; // A
            }

            return BytesToBitmap(outBytes, width, height);
        }

        private static Bitmap BytesToBitmap(byte[] pixels, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        private static Bitmap SolidColor(int width, int height, Color color)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.Clear(color);
            return bmp;
        }

        // ------------ BC1 / BC3 / BC5 HELPERS ------------

        private static Bitmap FromBC1(byte[] blocks, int width, int height)
        {
            var rgba = DecodeBC1(blocks, width, height);
            return BytesToBitmap(rgba, width, height);
        }

        private static Bitmap FromBC3(byte[] blocks, int width, int height)
        {
            var rgba = DecodeBC3(blocks, width, height);
            return BytesToBitmap(rgba, width, height);
        }

        private static Bitmap FromBC5(byte[] blocks, int width, int height)
        {
            var rgba = DecodeBC5AsNormals(blocks, width, height);
            return BytesToBitmap(rgba, width, height);
        }

        // Each BC1 block = 8 bytes, covers 4x4 texels
        private static byte[] DecodeBC1(byte[] data, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;

            var outRGBA = new byte[width * height * 4];
            int srcOffset = 0;

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    if (srcOffset + 8 > data.Length)
                        break;

                    ushort c0 = BitConverter.ToUInt16(data, srcOffset + 0);
                    ushort c1 = BitConverter.ToUInt16(data, srcOffset + 2);
                    uint codes = BitConverter.ToUInt32(data, srcOffset + 4);

                    var palette = new uint[4];
                    palette[0] = RGB565ToRGBA(c0, 255);
                    palette[1] = RGB565ToRGBA(c1, 255);

                    if (c0 > c1)
                    {
                        palette[2] = LerpRGBA(palette[0], palette[1], 1, 2);
                        palette[3] = LerpRGBA(palette[0], palette[1], 2, 1);
                    }
                    else
                    {
                        palette[2] = LerpRGBA(palette[0], palette[1], 1, 1);
                        palette[3] = 0x00000000; // transparent
                    }

                    for (int row = 0; row < 4; row++)
                    {
                        for (int col = 0; col < 4; col++)
                        {
                            int pixelIndex = row * 4 + col;
                            int code = (int)((codes >> (pixelIndex * 2)) & 0x3);

                            int x = bx * 4 + col;
                            int y = by * 4 + row;
                            if (x >= width || y >= height)
                                continue;

                            uint rgba = palette[code];
                            int dst = (y * width + x) * 4;

                            outRGBA[dst + 0] = (byte)((rgba >> 0) & 0xFF);   // B
                            outRGBA[dst + 1] = (byte)((rgba >> 8) & 0xFF);   // G
                            outRGBA[dst + 2] = (byte)((rgba >> 16) & 0xFF);  // R
                            outRGBA[dst + 3] = (byte)((rgba >> 24) & 0xFF);  // A
                        }
                    }

                    srcOffset += 8;
                }
            }

            return outRGBA;
        }

        // BC3 = BC1 color + explicit alpha block
        private static byte[] DecodeBC3(byte[] data, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;

            var outRGBA = new byte[width * height * 4];
            int srcOffset = 0;

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    if (srcOffset + 16 > data.Length)
                        break;

                    // --- Alpha block ---
                    byte a0 = data[srcOffset + 0];
                    byte a1 = data[srcOffset + 1];

                    ulong alphaBits = 0;
                    for (int i = 0; i < 6; i++)
                        alphaBits |= ((ulong)data[srcOffset + 2 + i]) << (8 * i);

                    byte[] alphaPalette = BuildAlphaPalette(a0, a1);

                    // --- Color block (BC1) ---
                    ushort c0 = BitConverter.ToUInt16(data, srcOffset + 8);
                    ushort c1 = BitConverter.ToUInt16(data, srcOffset + 10);
                    uint codes = BitConverter.ToUInt32(data, srcOffset + 12);

                    var colorPalette = new uint[4];
                    colorPalette[0] = RGB565ToRGBA(c0, 255);
                    colorPalette[1] = RGB565ToRGBA(c1, 255);
                    colorPalette[2] = LerpRGBA(colorPalette[0], colorPalette[1], 1, 2);
                    colorPalette[3] = LerpRGBA(colorPalette[0], colorPalette[1], 2, 1);

                    // --- Fill 4x4 texels ---
                    for (int row = 0; row < 4; row++)
                    {
                        for (int col = 0; col < 4; col++)
                        {
                            int pixelIndex = row * 4 + col;

                            int x = bx * 4 + col;
                            int y = by * 4 + row;
                            if (x >= width || y >= height)
                                continue;

                            int alphaIndex = (int)((alphaBits >> (pixelIndex * 3)) & 0x7);
                            byte a = alphaPalette[alphaIndex];

                            int colorCode = (int)((codes >> (pixelIndex * 2)) & 0x3);
                            uint rgba = colorPalette[colorCode];

                            int dst = (y * width + x) * 4;
                            outRGBA[dst + 0] = (byte)((rgba >> 0) & 0xFF);    // B
                            outRGBA[dst + 1] = (byte)((rgba >> 8) & 0xFF);    // G
                            outRGBA[dst + 2] = (byte)((rgba >> 16) & 0xFF);   // R
                            outRGBA[dst + 3] = a;                             // A
                        }
                    }

                    srcOffset += 16;
                }
            }

            return outRGBA;
        }

        // BC5: two BC4 blocks (R and G), usually used as normal maps.
        private static byte[] DecodeBC5AsNormals(byte[] data, int width, int height)
        {
            int blocksWide = (width + 3) / 4;
            int blocksHigh = (height + 3) / 4;

            var outRGBA = new byte[width * height * 4];
            int srcOffset = 0;

            for (int by = 0; by < blocksHigh; by++)
            {
                for (int bx = 0; bx < blocksWide; bx++)
                {
                    if (srcOffset + 16 > data.Length)
                        break;

                    // First 8 bytes = Red channel (BC4)
                    byte r0 = data[srcOffset + 0];
                    byte r1 = data[srcOffset + 1];
                    ulong rBits = 0;
                    for (int i = 0; i < 6; i++)
                        rBits |= ((ulong)data[srcOffset + 2 + i]) << (8 * i);
                    var rPalette = BuildAlphaPalette(r0, r1);

                    // Next 8 bytes = Green channel (BC4)
                    byte g0 = data[srcOffset + 8];
                    byte g1 = data[srcOffset + 9];
                    ulong gBits = 0;
                    for (int i = 0; i < 6; i++)
                        gBits |= ((ulong)data[srcOffset + 10 + i]) << (8 * i);
                    var gPalette = BuildAlphaPalette(g0, g1);

                    // 16 pixels in block
                    for (int row = 0; row < 4; row++)
                    {
                        for (int col = 0; col < 4; col++)
                        {
                            int pixelIndex = row * 4 + col;
                            int x = bx * 4 + col;
                            int y = by * 4 + row;
                            if (x >= width || y >= height)
                                continue;

                            int rIndex = (int)((rBits >> (pixelIndex * 3)) & 0x7);
                            int gIndex = (int)((gBits >> (pixelIndex * 3)) & 0x7);

                            byte rByte = rPalette[rIndex];
                            byte gByte = gPalette[gIndex];

                            // Map 0..255 → -1..1
                            float nx = (rByte / 255f) * 2f - 1f;
                            float ny = (gByte / 255f) * 2f - 1f;
                            float nzSq = 1f - (nx * nx + ny * ny);
                            float nz = nzSq > 0 ? (float)Math.Sqrt(nzSq) : 0f;

                            byte bz = (byte)Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0, 255);

                            int dst = (y * width + x) * 4;
                            outRGBA[dst + 0] = bz;       // B
                            outRGBA[dst + 1] = gByte;    // G
                            outRGBA[dst + 2] = rByte;    // R
                            outRGBA[dst + 3] = 255;      // A
                        }
                    }

                    srcOffset += 16;
                }
            }

            return outRGBA;
        }

        // ------------- SMALL UTILS -------------

        private static uint RGB565ToRGBA(ushort c, byte alpha)
        {
            int r = (c >> 11) & 0x1F;
            int g = (c >> 5) & 0x3F;
            int b = (c >> 0) & 0x1F;

            // expand to 0..255
            byte rr = (byte)((r * 255 + 15) / 31);
            byte gg = (byte)((g * 255 + 31) / 63);
            byte bb = (byte)((b * 255 + 15) / 31);

            return ((uint)alpha << 24) | ((uint)rr << 16) | ((uint)gg << 8) | bb;
        }

        private static uint LerpRGBA(uint c0, uint c1, int w0, int w1)
        {
            int total = w0 + w1;
            byte a0 = (byte)((c0 >> 24) & 0xFF);
            byte r0 = (byte)((c0 >> 16) & 0xFF);
            byte g0 = (byte)((c0 >> 8) & 0xFF);
            byte b0 = (byte)(c0 & 0xFF);

            byte a1 = (byte)((c1 >> 24) & 0xFF);
            byte r1 = (byte)((c1 >> 16) & 0xFF);
            byte g1 = (byte)((c1 >> 8) & 0xFF);
            byte b1 = (byte)(c1 & 0xFF);

            byte a = (byte)((a0 * w0 + a1 * w1) / total);
            byte r = (byte)((r0 * w0 + r1 * w1) / total);
            byte g = (byte)((g0 * w0 + g1 * w1) / total);
            byte b = (byte)((b0 * w0 + b1 * w1) / total);

            return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        private static byte[] BuildAlphaPalette(byte a0, byte a1)
        {
            var palette = new byte[8];
            palette[0] = a0;
            palette[1] = a1;

            if (a0 > a1)
            {
                // 6 interpolated values
                for (int i = 1; i <= 6; i++)
                    palette[i + 1] = (byte)(((6 - (i - 1)) * a0 + (i - 1) * a1) / 7);
            }
            else
            {
                // 4 interpolated + 0 + 255
                for (int i = 1; i <= 4; i++)
                    palette[i + 1] = (byte)(((4 - (i - 1)) * a0 + (i - 1) * a1) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }

            return palette;
        }
    }
}
