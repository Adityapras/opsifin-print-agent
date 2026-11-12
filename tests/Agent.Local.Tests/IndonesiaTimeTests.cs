using Agent.Local;

namespace Agent.Local.Tests;

public sealed class IndonesiaTimeTests
{
    [Fact]
    public void Utc_dikonversi_ke_wib_dengan_offset_tujuh_jam()
    {
        var utc = new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);

        var jakarta = IndonesiaTime.Convert(utc);

        Assert.Equal(TimeSpan.FromHours(7), jakarta.Offset);
        Assert.Equal(17, jakarta.Hour);
        Assert.Equal(30, jakarta.Minute);
    }
}
