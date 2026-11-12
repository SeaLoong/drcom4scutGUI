using Avalonia.Media;
using Xunit;

namespace drcom4scutGUI.UiTests;

public sealed class DesktopPaletteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothThemesKeepTextAndControlBoundariesReadable(bool dark)
    {
        DesktopPalette palette = new();
        palette.Apply(dark);
        foreach (SolidColorBrush surface in new[] { palette.Surface, palette.Paper, palette.Soft })
        {
            Assert.True(Contrast(palette.Ink.Color, surface.Color) >= 4.5);
            Assert.True(Contrast(palette.Muted.Color, surface.Color) >= 4.5);
            Assert.True(Contrast(palette.Error.Color, surface.Color) >= 4.5);
        }
        foreach (SolidColorBrush button in new[] { palette.Accent, palette.AccentHover, palette.AccentPressed })
            Assert.True(Contrast(palette.OnAccent.Color, button.Color) >= 4.5);
        Assert.True(Contrast(palette.Line.Color, palette.Paper.Color) >= 3);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte c) => c / 255d <= 0.04045 ? c / 255d / 12.92 : Math.Pow((c / 255d + 0.055) / 1.055, 2.4);
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double first = Luminance(a), second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
