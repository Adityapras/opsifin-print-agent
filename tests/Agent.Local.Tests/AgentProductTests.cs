using Agent.Local;

namespace Agent.Local.Tests;

public sealed class AgentProductTests
{
    [Fact]
    public void Versi_produk_selalu_mengikuti_metadata_assembly()
    {
        var assemblyVersion = typeof(AgentProduct).Assembly.GetName().Version;

        Assert.NotNull(assemblyVersion);
        Assert.Equal(
            $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}",
            AgentProduct.Version);
    }
}
