namespace Agent.Tests;

// Menjaga agar salinan kontrak di repo ini tetap memuat semua endpoint yang dipanggil agent.
// Bila tes ini gagal setelah kontrak diubah di repo Hub, sinkronkan contracts/agent-v1.yaml.
public class ContractTests
{
    private static readonly string ContractPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "agent-v1.yaml");

    [Theory]
    [InlineData("/enroll:")]
    [InlineData("/heartbeat:")]
    [InlineData("/jobs/claim:")]
    [InlineData("/jobs/{job_id}/events:")]
    [InlineData("/documents/{document_id}:")]
    [InlineData("/commands/{command_id}/result:")]
    public void Kontrak_memuat_endpoint_agent(string path)
    {
        var contract = File.ReadAllText(ContractPath);

        Assert.Contains($"  {path}", contract);
    }
}
