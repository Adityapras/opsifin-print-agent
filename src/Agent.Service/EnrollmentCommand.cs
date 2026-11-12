using System.Runtime.InteropServices;
using Agent.Hub;
using Agent.Local;

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
                "Usage: Agent.Service.exe enroll --hub-url <URL> --pairing-code <8-characters>");
            return 2;
        }

        if (pairingCode.Length != 8)
        {
            Console.Error.WriteLine("The pairing code must contain exactly 8 characters.");
            return 2;
        }

        var store = new DeviceIdentityStore();
        if (store.Exists)
        {
            Console.Error.WriteLine($"The agent is already enrolled. Its identity is stored at {store.Path}");
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
                    AgentProduct.Version,
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

            Console.WriteLine($"Enrollment completed successfully. Agent ID: {response.AgentId}");
            Console.WriteLine($"The encrypted identity is stored at {store.Path}");
            return 0;
        }
        catch (AgentApiException ex)
        {
            Console.Error.WriteLine(
                $"Enrollment was rejected by the Hub: HTTP {(int?)ex.StatusCode} {ex.ErrorCode} - {ex.Message}");
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Enrollment failed: {ex.Message}");
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
