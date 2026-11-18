using System;
using System.Security.Cryptography;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-4b (docs/work-orders/terrain-vegetation/T4.md, design D1 and D6): generates materials/biomes/alpine_rock_detail_normal.png, a tileable tangent-space normal map (OpenGL convention: +x along u, +y toward the top of
/// the image) derived deterministically from materials/biomes/alpine_rock_albedo.png: resample to 1024 x 1024 (Lanczos), luminance as height, remove the low frequencies (a wrapped box blur, two passes of radius 48 px, so detail from 1 cm to about 0.5 m on the 5 m tile stays),
/// wrapped Sobel gradient, scaled so the RMS of the slope is TargetRmsSlope, written as RGB8 (the importer compresses it to BC5, about 1.4 MiB with mipmaps). Prints the SHA-256 of the source and of the output.
/// Default: regenerate in memory and compare with the file on disk (pixel data); --write (after --) rewrites the file. Headless: tools/ground_rock_detail_normal.gd.
/// </summary>
public partial class GroundRockDetailNormal : RefCounted
{
    public const string SourcePath = "res://materials/biomes/alpine_rock_albedo.png";
    public const string OutputPath = "res://materials/biomes/alpine_rock_detail_normal.png";
    private const int Size = 1024;
    private const int BlurRadius = 48;
    private const float TargetRmsSlope = 0.30f;
    private const float MaxSlope = 0.9f;

    public int Run()
    {
        bool write = Array.IndexOf(OS.GetCmdlineUserArgs(), "--write") >= 0;
        try
        {
            string sourceFile = ProjectSettings.GlobalizePath(SourcePath);
            string outputFile = ProjectSettings.GlobalizePath(OutputPath);
            Image source = Image.LoadFromFile(sourceFile) ?? throw new InvalidOperationException("source texture missing");
            Image generated = Generate(source);
            string sourceSha = Sha(System.IO.File.ReadAllBytes(sourceFile));
            if (write)
            {
                generated.SavePng(outputFile);
                Console.WriteLine($"GROUND_ROCK_DETAIL_NORMAL wrote {OutputPath}");
            }
            Image onDisk = Image.LoadFromFile(outputFile);
            bool same = onDisk != null && onDisk.GetFormat() == generated.GetFormat() && onDisk.GetWidth() == Size && onDisk.GetHeight() == Size
                && onDisk.GetData().AsSpan().SequenceEqual(generated.GetData());
            Console.WriteLine($"GROUND_ROCK_DETAIL_NORMAL source_sha256={sourceSha} output_sha256={Sha(System.IO.File.ReadAllBytes(outputFile))} pixels_sha256={Sha(generated.GetData())}");
            if (!same)
            {
                Console.Error.WriteLine("GROUND_ROCK_DETAIL_NORMAL=FAIL the file on disk differs from the regenerated pixels (run with -- --write)");
                return 1;
            }
            Console.WriteLine("GROUND_ROCK_DETAIL_NORMAL=PASS");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("GROUND_ROCK_DETAIL_NORMAL=FAIL " + e);
            return 1;
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static Image Generate(Image source)
    {
        Image work = (Image)source.Duplicate();
        work.Convert(Image.Format.Rgb8);
        work.Resize(Size, Size, Image.Interpolation.Lanczos);
        byte[] rgb = work.GetData();
        var height = new float[Size * Size];
        for (int i = 0; i < height.Length; i++)
            height[i] = (0.2126f * rgb[i * 3] + 0.7152f * rgb[i * 3 + 1] + 0.0722f * rgb[i * 3 + 2]) / 255.0f;
        float[] low = Blur(Blur(height));
        for (int i = 0; i < height.Length; i++) height[i] -= low[i];

        var gradU = new float[Size * Size];
        var gradV = new float[Size * Size];
        double sumSquares = 0.0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float H(int dx, int dy) => height[((y + dy + Size) % Size) * Size + (x + dx + Size) % Size];
                float gu = ((H(1, -1) + 2.0f * H(1, 0) + H(1, 1)) - (H(-1, -1) + 2.0f * H(-1, 0) + H(-1, 1))) * 0.125f;
                float gv = ((H(-1, 1) + 2.0f * H(0, 1) + H(1, 1)) - (H(-1, -1) + 2.0f * H(0, -1) + H(1, -1))) * 0.125f;
                gradU[y * Size + x] = gu;
                gradV[y * Size + x] = gv;
                sumSquares += gu * gu + gv * gv;
            }
        }
        float scale = TargetRmsSlope / (float)Math.Sqrt(sumSquares / (Size * Size));
        var bytes = new byte[Size * Size * 3];
        for (int i = 0; i < Size * Size; i++)
        {
            // Height is brightness: n = (-dh/du, +dh/dv (v runs down the image, +y is up), 1).
            float nx = -gradU[i] * scale, ny = gradV[i] * scale;
            float length = (float)Math.Sqrt(nx * nx + ny * ny);
            if (length > MaxSlope) { nx *= MaxSlope / length; ny *= MaxSlope / length; }
            float nz = (float)Math.Sqrt(1.0f - Math.Min(nx * nx + ny * ny, 0.99f));
            bytes[i * 3] = Encode(nx);
            bytes[i * 3 + 1] = Encode(ny);
            bytes[i * 3 + 2] = Encode(nz);
        }
        return Image.CreateFromData(Size, Size, false, Image.Format.Rgb8, bytes);
    }

    private static byte Encode(float v) => (byte)Math.Clamp((int)MathF.Round((v * 0.5f + 0.5f) * 255.0f), 0, 255);

    /// <summary>Wrapped box blur of radius BlurRadius (separable).</summary>
    private static float[] Blur(float[] input)
    {
        var temp = new float[input.Length];
        var output = new float[input.Length];
        int width = BlurRadius * 2 + 1;
        for (int y = 0; y < Size; y++)
        {
            float sum = 0.0f;
            for (int k = -BlurRadius; k <= BlurRadius; k++) sum += input[y * Size + (k + Size) % Size];
            for (int x = 0; x < Size; x++)
            {
                temp[y * Size + x] = sum / width;
                sum += input[y * Size + (x + BlurRadius + 1) % Size] - input[y * Size + (x - BlurRadius + Size) % Size];
            }
        }
        for (int x = 0; x < Size; x++)
        {
            float sum = 0.0f;
            for (int k = -BlurRadius; k <= BlurRadius; k++) sum += temp[((k + Size) % Size) * Size + x];
            for (int y = 0; y < Size; y++)
            {
                output[y * Size + x] = sum / width;
                sum += temp[((y + BlurRadius + 1) % Size) * Size + x] - temp[((y - BlurRadius + Size) % Size) * Size + x];
            }
        }
        return output;
    }
}
