using Agent.Printing;

namespace Agent.Tests;

public sealed class PageLayoutCalculatorTests
{
    private static readonly PrinterPageMetrics A4At300Dpi = new(2480, 3508, 300, 300);

    [Fact]
    public void NoScale_PreservesPdfPhysicalSize()
    {
        var layout = PageLayoutCalculator.Calculate(
            A4At300Dpi,
            595.28f,
            841.89f,
            PrintScaleMode.NoScale);

        Assert.Equal(0, layout.Rotation);
        Assert.InRange(layout.Width, 2479, 2481);
        Assert.InRange(layout.Height, 3507, 3509);
    }

    [Fact]
    public void LandscapePage_RotatesToUsePortraitPaper()
    {
        var layout = PageLayoutCalculator.Calculate(
            A4At300Dpi,
            841.89f,
            595.28f,
            PrintScaleMode.Shrink);

        Assert.Equal(1, layout.Rotation);
        Assert.True(layout.Height > layout.Width);
        Assert.True(layout.Width <= A4At300Dpi.PrintableWidth);
        Assert.True(layout.Height <= A4At300Dpi.PrintableHeight);
    }

    [Fact]
    public void Fit_EnlargesSmallPageAndCentersIt()
    {
        var layout = PageLayoutCalculator.Calculate(
            A4At300Dpi,
            200,
            200,
            PrintScaleMode.Fit);

        Assert.Equal(A4At300Dpi.PrintableWidth, layout.Width);
        Assert.Equal(0, layout.X);
        Assert.True(layout.Y > 0);
    }

    [Fact]
    public void Shrink_DoesNotEnlargeSmallPage()
    {
        var layout = PageLayoutCalculator.Calculate(
            A4At300Dpi,
            200,
            200,
            PrintScaleMode.Shrink);

        Assert.InRange(layout.Width, 832, 834);
        Assert.InRange(layout.Height, 832, 834);
        Assert.True(layout.X > 0);
        Assert.True(layout.Y > 0);
    }
}
