namespace Agent.Printing;

public readonly record struct PrinterPageMetrics(
    int PrintableWidth,
    int PrintableHeight,
    int DpiX,
    int DpiY);

public readonly record struct PageLayout(
    int X,
    int Y,
    int Width,
    int Height,
    int Rotation);

public static class PageLayoutCalculator
{
    public static PageLayout Calculate(
        PrinterPageMetrics printer,
        float pageWidthPoints,
        float pageHeightPoints,
        PrintScaleMode scaleMode)
    {
        if (printer.PrintableWidth <= 0 || printer.PrintableHeight <= 0
            || printer.DpiX <= 0 || printer.DpiY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(printer));
        }

        if (pageWidthPoints <= 0 || pageHeightPoints <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageWidthPoints));
        }

        var normalScale = Math.Min(
            printer.PrintableWidth / (pageWidthPoints * printer.DpiX / 72d),
            printer.PrintableHeight / (pageHeightPoints * printer.DpiY / 72d));
        var rotatedScale = Math.Min(
            printer.PrintableWidth / (pageHeightPoints * printer.DpiX / 72d),
            printer.PrintableHeight / (pageWidthPoints * printer.DpiY / 72d));
        var rotate = rotatedScale > normalScale;

        var widthPoints = rotate ? pageHeightPoints : pageWidthPoints;
        var heightPoints = rotate ? pageWidthPoints : pageHeightPoints;
        var naturalWidth = widthPoints * printer.DpiX / 72d;
        var naturalHeight = heightPoints * printer.DpiY / 72d;
        var fitScale = rotate ? rotatedScale : normalScale;
        var scale = scaleMode switch
        {
            PrintScaleMode.NoScale => 1d,
            PrintScaleMode.Fit => fitScale,
            PrintScaleMode.Shrink => Math.Min(1d, fitScale),
            _ => throw new ArgumentOutOfRangeException(nameof(scaleMode)),
        };

        var width = Math.Max(1, (int)Math.Round(naturalWidth * scale));
        var height = Math.Max(1, (int)Math.Round(naturalHeight * scale));
        return new PageLayout(
            (printer.PrintableWidth - width) / 2,
            (printer.PrintableHeight - height) / 2,
            width,
            height,
            rotate ? 1 : 0);
    }
}
