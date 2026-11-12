using Microsoft.Win32;

namespace Agent.Local;

public static class MachineIdentity
{
    public static string ReadMachineGuid()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false)
            ?? throw new InvalidOperationException("The MachineGuid registry key was not found.");
        return key.GetValue("MachineGuid") as string
            ?? throw new InvalidOperationException("The MachineGuid registry value was not found.");
    }
}
