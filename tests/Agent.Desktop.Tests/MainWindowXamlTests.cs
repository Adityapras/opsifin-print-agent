namespace Agent.Desktop.Tests;

public sealed class MainWindowXamlTests
{
    [Fact]
    public void Progress_update_read_only_harus_memakai_binding_one_way()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Agent.Desktop", "MainWindow.xaml"));

        Assert.Contains(
            "Value=\"{Binding UpdateProgress, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Header_menampilkan_waktu_refresh_terakhir()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Agent.Desktop", "MainWindow.xaml"));

        Assert.Contains("Text=\"{Binding LastStatusRefresh}\"", xaml, StringComparison.Ordinal);
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
