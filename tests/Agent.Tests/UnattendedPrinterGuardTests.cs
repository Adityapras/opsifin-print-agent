using Agent.Printing;

namespace Agent.Tests;

public sealed class UnattendedPrinterGuardTests
{
    [Theory]
    [InlineData("Microsoft Print To PDF", "PORTPROMPT:")]
    [InlineData("Microsoft XPS Document Writer v4", "PORTPROMPT:")]
    [InlineData("Microsoft Shared Fax Driver", "SHRFAX:")]
    [InlineData("Send to Microsoft OneNote 16 Driver", "NUL:")]
    public void Menolak_printer_yang_memerlukan_dialog_interaktif(string driver, string port)
    {
        var reason = UnattendedPrinterGuard.FindUnsupportedReason(driver, [port]);

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("EPSON Universal Print Driver", "USB001")]
    [InlineData("HP Universal Printing PCL 6", "IP_192.168.1.20")]
    [InlineData("Generic / Text Only", "LPT1:")]
    public void Mengizinkan_printer_fisik_dan_network_non_interaktif(string driver, string port)
    {
        var reason = UnattendedPrinterGuard.FindUnsupportedReason(driver, [port]);

        Assert.Null(reason);
    }
}
