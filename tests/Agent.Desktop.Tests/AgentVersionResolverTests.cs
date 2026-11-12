using Agent.Desktop.Services;

namespace Agent.Desktop.Tests;

public class AgentVersionResolverTests
{
    [Fact]
    public void Mengutamakan_versi_service_agar_gui_lama_menampilkan_hasil_ota_terbaru()
    {
        var version = AgentVersionResolver.Resolve("0.3.8", "0.3.9");

        Assert.Equal("0.3.9", version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Memakai_versi_control_saat_health_service_belum_tersedia(string? serviceVersion)
    {
        var version = AgentVersionResolver.Resolve("0.3.9", serviceVersion);

        Assert.Equal("0.3.9", version);
    }
}
