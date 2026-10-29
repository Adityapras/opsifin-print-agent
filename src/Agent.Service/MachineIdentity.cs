using Microsoft.Win32;

namespace Agent.Service;

internal static class MachineIdentity
{
    public static string ReadMachineGuid()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false)
            ?? throw new InvalidOperationException("Registry MachineGuid tidak ditemukan.");
        return key.GetValue("MachineGuid") as string
            ?? throw new InvalidOperationException("Nilai registry MachineGuid tidak ditemukan.");
    }
}
