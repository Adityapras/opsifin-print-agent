namespace Agent.Desktop.Tests;

public sealed class InstallerAuthoringTests
{
    [Fact]
    public void Bootstrapper_membungkus_msi_dan_mempertahankan_msi_untuk_ota()
    {
        var root = FindRepositoryRoot();
        var bundle = File.ReadAllText(Path.Combine(root, "installer", "Bundle.wxs"));
        var package = File.ReadAllText(Path.Combine(root, "installer", "Package.wxs"));
        var script = File.ReadAllText(Path.Combine(root, "tools", "build-installer.ps1"));

        Assert.Contains("<MsiPackage", bundle, StringComparison.Ordinal);
        Assert.Contains("OPSIFIN_BUNDLE_INSTALL", bundle, StringComparison.Ordinal);
        Assert.Contains("OPSIFIN_LAUNCH_AFTER_INSTALL", bundle, StringComparison.Ordinal);
        Assert.Contains("WixBundleUILevel = 4", bundle, StringComparison.Ordinal);
        Assert.Contains("Opsifin.Agent.Control.exe", bundle, StringComparison.Ordinal);
        Assert.Contains("CloseAgentControl", package, StringComparison.Ordinal);
        Assert.Contains("Visible=\"yes\"", bundle, StringComparison.Ordinal);
        Assert.Contains("win-x64.exe", script, StringComparison.Ordinal);
        Assert.Contains("win-x64.msi", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrapper_menolak_windows_32_bit_dan_versi_lama()
    {
        var root = FindRepositoryRoot();
        var bundle = File.ReadAllText(Path.Combine(root, "installer", "Bundle.wxs"));

        Assert.Contains("VersionNT64 &gt;= v10.0", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void Semua_jalur_upgrade_menutup_agent_control_lama()
    {
        var root = FindRepositoryRoot();
        var packagePath = Path.Combine(root, "installer", "Package.wxs");
        var document = System.Xml.Linq.XDocument.Load(packagePath);
        var closeApplication = document
            .Descendants()
            .Single(element => element.Name.LocalName == "CloseApplication");

        Assert.Equal("Opsifin.Agent.Control.exe", closeApplication.Attribute("Target")?.Value);
        Assert.Null(closeApplication.Attribute("Condition"));
        Assert.Equal("0", closeApplication.Attribute("TerminateProcess")?.Value);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpsifinPrintAgent.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be found.");
    }
}
