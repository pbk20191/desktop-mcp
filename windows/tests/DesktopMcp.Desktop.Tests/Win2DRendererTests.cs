using DesktopMcp.Desktop.Imaging;

namespace DesktopMcp.Desktop.Tests;

[Trait("Category", "Desktop")]
public class Win2DRendererTests
{
    private static readonly (byte R, byte G, byte B, byte A) Gray = (0x80, 0x80, 0x80, 0xFF);

    public Win2DRendererTests() => AppRuntime.EnsureInitialized();

    [Fact]
    public async Task EncodesAPngOfTheSameSize()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(120, 90), 1.0, []);
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((120, 90), (decoded.Width, decoded.Height));
        Assert.Equal(Gray, decoded.Pixel(60, 45));
    }

    [Fact]
    public async Task ScalesDown()
    {
        var png = await new Win2DRenderer().RenderPngAsync(Solid(200, 100), 0.5, []);
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal((100, 50), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task DrawsTheBoxInThePaletteColourAndLeavesTheInsideAlone()
    {
        var annotations = new[] { new Annotation(new PixelRect(40, 50, 140, 120), "0") };
        var png = await new Win2DRenderer().RenderPngAsync(Solid(200, 150), 1.0, annotations);
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal(Opaque(Win2DRenderer.Palette[0]), decoded.Pixel(40, 90));
        Assert.Equal(Gray, decoded.Pixel(90, 90));
    }

    [Fact]
    public async Task BoxesScaleWithTheImage()
    {
        var annotations = new[] { new Annotation(new PixelRect(80, 100, 280, 240), "0") };
        var png = await new Win2DRenderer().RenderPngAsync(Solid(400, 300), 0.5, annotations);
        var decoded = await PngTestDecoder.DecodeAsync(png);
        Assert.Equal(Opaque(Win2DRenderer.Palette[0]), decoded.Pixel(40, 85));
    }

    private static CapturedImage Solid(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = Gray.B;
            bgra[i + 1] = Gray.G;
            bgra[i + 2] = Gray.R;
            bgra[i + 3] = Gray.A;
        }
        return new CapturedImage(width, height, bgra);
    }

    private static (byte R, byte G, byte B, byte A) Opaque((byte R, byte G, byte B) c) => (c.R, c.G, c.B, 0xFF);
}
