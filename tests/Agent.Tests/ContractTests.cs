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

    [Fact]
    public void Kontrak_memuat_metadata_aktivitas_cetak_dan_interval_source()
    {
        var contract = File.ReadAllText(ContractPath);

        Assert.Contains("source_poll_interval_s:", contract);
        Assert.Contains("display: { $ref: '#/components/schemas/JobDisplay' }", contract);
        Assert.Contains("invoice_no: { type: string", contract);
        Assert.Contains("item_type: { type: string", contract);
    }
}
