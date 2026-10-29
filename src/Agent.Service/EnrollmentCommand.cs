using System.Runtime.InteropServices;
using Agent.Hub;

namespace Agent.Service;

internal static class EnrollmentCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var hubUrl = ReadOption(args, "--hub-url");
        var pairingCode = ReadOption(args, "--pairing-code");
        if (hubUrl is null || pairingCode is null)
        {
            Console.Error.WriteLine(
                "Pemakaian: Agent.Service.exe enroll --hub-url <URL> --pairing-code <8-karakter>");
            return 2;
        }

        if (pairingCode.Length != 8)
        {
            Console.Error.WriteLine("Pairing code harus tepat 8 karakter.");
            return 2;
        }

        var store = new DeviceIdentityStore();
        if (store.Exists)
        {
            Console.Error.WriteLine($"Agent sudah di-enroll. Identity tersimpan di {store.Path}");
            return 3;
        }

        try
        {
            var baseAddress = AgentHubClient.NormalizeBaseAddress(hubUrl);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new AgentHubClient(httpClient);
            var response = await client.EnrollAsync(
                baseAddress,
                new EnrollRequest(
                    pairingCode,
                    Environment.MachineName,
                    RuntimeInformation.OSDescription,
                    AgentMetadata.Version,
                    MachineIdentity.ReadMachineGuid()),
                CancellationToken.None);
            await store.SaveAsync(
                new DeviceIdentity(
                    response.AgentId,
                    response.TenantId,
                    response.SiteId,
                    baseAddress,
                    response.DeviceSecret),
                CancellationToken.None);

            Console.WriteLine($"Enrollment berhasil. Agent ID: {response.AgentId}");
            Console.WriteLine($"Identity terenkripsi tersimpan di {store.Path}");
            return 0;
        }
        catch (AgentApiException ex)
        {
            Console.Error.WriteLine(
                $"Enrollment ditolak Hub: HTTP {(int?)ex.StatusCode} {ex.ErrorCode} - {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Enrollment gagal: {ex.Message}");
            return 5;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
