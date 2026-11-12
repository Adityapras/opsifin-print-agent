using Agent.Local;

namespace Agent.Local.Tests;

public sealed class DeviceIdentityStoreTests
{
    [Fact]
    public async Task Reset_memindahkan_identity_ke_arsip_dan_mengosongkan_path_aktif()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opsifin-agent-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "identity.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, "identity-encrypted-placeholder");
            var store = new DeviceIdentityStore(path);

            var archivePath = store.ArchiveExisting();

            Assert.False(store.Exists);
            Assert.NotNull(archivePath);
            Assert.True(File.Exists(archivePath));
            Assert.Equal("identity-encrypted-placeholder", await File.ReadAllTextAsync(archivePath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
