using Agent.Hub;

namespace Agent.Tests;

public class PrinterStatusMapperTests
{
    [Theory]
    [InlineData(0x00000000u, 0u, PrinterStatusMapper.Online)]
    [InlineData(0x00000001u, 0u, PrinterStatusMapper.Online)] // PAUSED bukan error
    [InlineData(0x01000000u, 0u, PrinterStatusMapper.Online)] // POWER_SAVE
    [InlineData(0x00000008u, 0u, PrinterStatusMapper.PaperJam)]
    [InlineData(0x00000010u, 0u, PrinterStatusMapper.PaperOut)]
    [InlineData(0x00040000u, 0u, PrinterStatusMapper.NoToner)]
    [InlineData(0x00400000u, 0u, PrinterStatusMapper.DoorOpen)]
    [InlineData(0x00000080u, 0u, PrinterStatusMapper.Offline)]
    [InlineData(0x02000000u, 0u, PrinterStatusMapper.Offline)] // SERVER_OFFLINE
    [InlineData(0x00000000u, 0x00000400u, PrinterStatusMapper.Offline)] // atribut WORK_OFFLINE
    [InlineData(0x00000002u, 0u, PrinterStatusMapper.Error)]
    [InlineData(0x00100000u, 0u, PrinterStatusMapper.Error)] // USER_INTERVENTION
    public void Memetakan_flag_ke_status_kontrak(uint status, uint attributes, string expected)
    {
        Assert.Equal(expected, PrinterStatusMapper.Map(status, attributes));
    }

    [Fact]
    public void Penyebab_fisik_didahulukan_dari_offline_dan_error()
    {
        // Banyak driver menyalakan ERROR bersamaan dengan PAPER_OUT.
        Assert.Equal(PrinterStatusMapper.PaperOut, PrinterStatusMapper.Map(0x00000010u | 0x00000002u | 0x00000080u, 0));
    }

    [Fact]
    public void Semua_nilai_ada_di_enum_kontrak()
    {
        var contract = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "agent-v1.yaml"));
        string[] values =
        [
            PrinterStatusMapper.Online, PrinterStatusMapper.Offline, PrinterStatusMapper.PaperOut,
            PrinterStatusMapper.PaperJam, PrinterStatusMapper.NoToner, PrinterStatusMapper.DoorOpen,
            PrinterStatusMapper.Error,
        ];

        Assert.Contains("enum: [ONLINE, OFFLINE, PAPER_OUT, PAPER_JAM, NO_TONER, DOOR_OPEN, ERROR, UNKNOWN]", contract);
        Assert.All(values, v => Assert.Contains(v, contract));
    }
}
