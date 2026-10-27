namespace Agent.Printing;

/// <summary>Nama flag <c>PRINTER_INFO_2.Status</c> dan <c>.Attributes</c> untuk log dan laporan spike.</summary>
public static class PrinterFlags
{
    private static readonly (uint Flag, string Name)[] StatusNames =
    [
        (0x00000001, "PAUSED"),
        (0x00000002, "ERROR"),
        (0x00000004, "PENDING_DELETION"),
        (0x00000008, "PAPER_JAM"),
        (0x00000010, "PAPER_OUT"),
        (0x00000020, "MANUAL_FEED"),
        (0x00000040, "PAPER_PROBLEM"),
        (0x00000080, "OFFLINE"),
        (0x00000100, "IO_ACTIVE"),
        (0x00000200, "BUSY"),
        (0x00000400, "PRINTING"),
        (0x00000800, "OUTPUT_BIN_FULL"),
        (0x00001000, "NOT_AVAILABLE"),
        (0x00002000, "WAITING"),
        (0x00004000, "PROCESSING"),
        (0x00008000, "INITIALIZING"),
        (0x00010000, "WARMING_UP"),
        (0x00020000, "TONER_LOW"),
        (0x00040000, "NO_TONER"),
        (0x00080000, "PAGE_PUNT"),
        (0x00100000, "USER_INTERVENTION"),
        (0x00200000, "OUT_OF_MEMORY"),
        (0x00400000, "DOOR_OPEN"),
        (0x00800000, "SERVER_UNKNOWN"),
        (0x01000000, "POWER_SAVE"),
        (0x02000000, "SERVER_OFFLINE"),
        (0x04000000, "DRIVER_UPDATE_NEEDED"),
    ];

    // Atribut yang membedakan asal printer (per mesin, per user, push GPO, redirect RDP)
    // adalah yang paling penting untuk B2.
    private static readonly (uint Flag, string Name)[] AttributeNames =
    [
        (0x00000001, "QUEUED"),
        (0x00000002, "DIRECT"),
        (0x00000004, "DEFAULT"),
        (0x00000008, "SHARED"),
        (0x00000010, "NETWORK"),
        (0x00000020, "HIDDEN"),
        (0x00000040, "LOCAL"),
        (0x00000400, "WORK_OFFLINE"),
        (0x00000800, "ENABLE_BIDI"),
        (0x00001000, "RAW_ONLY"),
        (0x00002000, "PUBLISHED"),
        (0x00004000, "FAX"),
        (0x00008000, "TS"),
        (0x00020000, "PUSHED_USER"),
        (0x00040000, "PUSHED_MACHINE"),
        (0x00080000, "MACHINE"),
        (0x00100000, "FRIENDLY_NAME"),
        (0x00200000, "TS_GENERIC_DRIVER"),
        (0x00400000, "PER_USER"),
        (0x00800000, "ENTERPRISE_CLOUD"),
    ];

    public static IReadOnlyList<string> DescribeStatus(uint status) => Describe(status, StatusNames);

    public static IReadOnlyList<string> DescribeAttributes(uint attributes) => Describe(attributes, AttributeNames);

    private static List<string> Describe(uint value, (uint Flag, string Name)[] names)
    {
        var result = new List<string>();
        var known = 0u;
        foreach (var (flag, name) in names)
        {
            known |= flag;
            if ((value & flag) != 0)
            {
                result.Add(name);
            }
        }

        // Bit yang tidak dikenal tetap ditampilkan supaya driver aneh terlihat di laporan.
        if ((value & ~known) != 0)
        {
            result.Add($"0x{value & ~known:X8}");
        }

        return result;
    }
}
