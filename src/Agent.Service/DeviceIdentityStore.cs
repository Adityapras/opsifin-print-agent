using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Agent.Service;

public sealed class DeviceIdentity(
    Guid agentId,
    Guid tenantId,
    Guid siteId,
    Uri hubBaseAddress,
    string deviceSecret)
{
    private string currentDeviceSecret = deviceSecret;

    public Guid AgentId { get; } = agentId;

    public Guid TenantId { get; } = tenantId;

    public Guid SiteId { get; } = siteId;

    public Uri HubBaseAddress { get; } = hubBaseAddress;

    public string DeviceSecret => Volatile.Read(ref currentDeviceSecret);

    public string BearerToken => $"{AgentId}.{DeviceSecret}";

    public DeviceIdentity WithDeviceSecret(string newDeviceSecret) => new(
        AgentId,
        TenantId,
        SiteId,
        HubBaseAddress,
        newDeviceSecret);

    public void UseDeviceSecret(string newDeviceSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newDeviceSecret);
        Volatile.Write(ref currentDeviceSecret, newDeviceSecret);
    }

    public override string ToString() =>
        $"DeviceIdentity {{ AgentId = {AgentId}, TenantId = {TenantId}, SiteId = {SiteId} }}";
}

internal sealed record StoredDeviceIdentity(
    Guid AgentId,
    Guid TenantId,
    Guid SiteId,
    string HubBaseAddress,
    string ProtectedDeviceSecret);

public sealed class DeviceIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Opsifin.PrintAgent.DeviceSecret.v1");

    public DeviceIdentityStore()
        : this(AgentStatePaths.IdentityFile)
    {
    }

    internal DeviceIdentityStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public bool Exists => File.Exists(Path);

    public async Task SaveAsync(DeviceIdentity identity, CancellationToken cancellationToken)
    {
        var deviceSecret = identity.DeviceSecret;
        var secretBytes = Encoding.UTF8.GetBytes(deviceSecret);
        byte[] protectedSecret;
        try
        {
            protectedSecret = ProtectedData.Protect(
                secretBytes,
                Entropy,
                DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        var stored = new StoredDeviceIdentity(
            identity.AgentId,
            identity.TenantId,
            identity.SiteId,
            identity.HubBaseAddress.AbsoluteUri,
            Convert.ToBase64String(protectedSecret));

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Path identity tidak memiliki direktori.");
        if (string.Equals(directory, AgentStatePaths.StateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AgentStatePaths.EnsureSecureStateDirectory();
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
        var temporaryPath = $"{Path}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                RestrictToSystemAndAdministrators(temporaryPath);
                await JsonSerializer.SerializeAsync(
                    stream,
                    stored,
                    AgentServiceJsonContext.Default.StoredDeviceIdentity,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, Path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void RestrictToSystemAndAdministrators(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public async Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        await using var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stored = await JsonSerializer.DeserializeAsync(
            stream,
            AgentServiceJsonContext.Default.StoredDeviceIdentity,
            cancellationToken)
            ?? throw new InvalidDataException("File identity kosong.");
        var secret = ProtectedData.Unprotect(
            Convert.FromBase64String(stored.ProtectedDeviceSecret),
            Entropy,
            DataProtectionScope.LocalMachine);
        try
        {
            return new DeviceIdentity(
                stored.AgentId,
                stored.TenantId,
                stored.SiteId,
                new Uri(stored.HubBaseAddress, UriKind.Absolute),
                Encoding.UTF8.GetString(secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
