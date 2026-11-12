namespace Agent.Local;

public static class IndonesiaTime
{
    private static readonly TimeZoneInfo JakartaTimeZone = ResolveJakartaTimeZone();

    public const string IanaId = "Asia/Jakarta";

    public static DateTimeOffset Now(TimeProvider timeProvider) =>
        Convert(timeProvider.GetUtcNow());

    public static DateTimeOffset Convert(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, JakartaTimeZone);

    private static TimeZoneInfo ResolveJakartaTimeZone()
    {
        // Service berjalan di Windows, sedangkan test dapat berjalan lintas platform.
        // Kedua ID dipertahankan agar offset operasional konsisten UTC+07:00.
        foreach (var id in new[] { "SE Asia Standard Time", IanaId })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // Coba ID platform berikutnya.
            }
            catch (InvalidTimeZoneException)
            {
                // Database zona waktu rusak ditangani oleh fallback offset tetap di bawah.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            IanaId,
            TimeSpan.FromHours(7),
            "Waktu Indonesia Barat",
            "Waktu Indonesia Barat");
    }
}
