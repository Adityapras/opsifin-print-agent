namespace Agent.Hub;

public static class AtomicFileCommit
{
    public static void CloseAndMove(
        FileStream temporaryFile,
        string temporaryPath,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(temporaryFile);

        // Windows menolak rename file yang masih dibuka dengan FileShare.None. Penutupan harus
        // eksplisit karena using declaration baru membuang stream pada akhir scope pemanggil.
        temporaryFile.Close();
        File.Move(temporaryPath, destinationPath, true);
    }
}
