using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

if (args.Length == 3 && args[0] == "--verify")
{
    VerifyIcons(args[1], args[2]);
    return;
}
if (args.Length != 3 || args[1] != "--source" || !File.Exists(args[2]))
    throw new ArgumentException("Usage: IconGenerator output.ico --source brand.png | --verify app.exe expected.ico");

// Explicit source only: a missing/corrupt logo must fail the build, never use a placeholder.
var path = Path.GetFullPath(args[0]);
using var source = new Bitmap(Path.GetFullPath(args[2]));
int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
var frames = new List<byte[]>();
foreach (int size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
        float width = source.Width * scale, height = source.Height * scale;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(source, Rectangle.Round(new RectangleF((size - width) / 2, (size - height) / 2, width, height)),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }
    using var frame = new MemoryStream();
    bitmap.Save(frame, ImageFormat.Png);
    frames.Add(frame.ToArray());
}
Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using (var writer = new BinaryWriter(File.Create(path)))
{
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
    uint offset = (uint)(6 + 16 * sizes.Length);
    for (int i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write((uint)frames[i].Length); writer.Write(offset);
        offset += (uint)frames[i].Length;
    }
    foreach (var frame in frames) writer.Write(frame);
}
Console.WriteLine($"Brand icon: {args[2]} -> {path} ({sizes.Length} sizes)");

static void VerifyIcons(string executable, string expected)
{
    // Read each EXE directly, bypassing Explorer's potentially stale icon cache.
    foreach (int size in new[] { 16, 20, 24, 32, 48, 64, 128, 256 })
    {
        var actualHandles = new IntPtr[1];
        var expectedHandles = new IntPtr[1];
        try
        {
            if (Native.PrivateExtractIcons(Path.GetFullPath(executable), 0, size, size, actualHandles, new uint[1], 1, 0) != 1 || actualHandles[0] == IntPtr.Zero ||
                Native.PrivateExtractIcons(Path.GetFullPath(expected), 0, size, size, expectedHandles, new uint[1], 1, 0) != 1 || expectedHandles[0] == IntPtr.Zero)
                throw new Exception($"Cannot extract {size}px icon: {executable}");
            using var actualIcon = Icon.FromHandle(actualHandles[0]);
            using var expectedIcon = Icon.FromHandle(expectedHandles[0]);
            using var actualBitmap = actualIcon.ToBitmap();
            using var expectedBitmap = expectedIcon.ToBitmap();
            if (actualBitmap.Size != expectedBitmap.Size) throw new Exception("Icon dimensions differ");
            bool visible = false;
            for (int y = 0; y < actualBitmap.Height; y++)
                for (int x = 0; x < actualBitmap.Width; x++)
                {
                    var pixel = actualBitmap.GetPixel(x, y);
                    if (pixel != expectedBitmap.GetPixel(x, y)) throw new Exception($"Wrong embedded icon in {executable} at {size}px");
                    visible |= pixel.A > 0;
                }
            if (!visible) throw new Exception("Icon is empty");
        }
        finally
        {
            if (actualHandles[0] != IntPtr.Zero) Native.DestroyIcon(actualHandles[0]);
            if (expectedHandles[0] != IntPtr.Zero) Native.DestroyIcon(expectedHandles[0]);
        }
    }
    Console.WriteLine($"PASS: embedded brand icon in {executable}, 16–256px");
}

internal static class Native
{
    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode)]
    internal static extern uint PrivateExtractIcons(string file, int index, int width, int height, [Out] IntPtr[] icons, [Out] uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
}
