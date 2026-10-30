using System.Security.Cryptography;
using System.Text;

internal static class ConnectionStringStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CMIS", "CodeGen");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "connection.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CMIS.CodeGen.ConnectionString.v1");

    public static bool Exists => File.Exists(FilePath);

    public static void Save(string connectionString)
    {
        EnsureWindows();

        var plainBytes = Encoding.UTF8.GetBytes(connectionString);
        var protectedBytes = ProtectedData.Protect(
            plainBytes, Entropy, DataProtectionScope.CurrentUser);

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporaryPath = FilePath + "." + Path.GetRandomFileName() + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, protectedBytes);
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public static string? Load()
    {
        EnsureWindows();

        if (!File.Exists(FilePath))
            return null;

        var protectedBytes = File.ReadAllBytes(FilePath);
        var plainBytes = ProtectedData.Unprotect(
            protectedBytes, Entropy, DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public static void Clear()
    {
        EnsureWindows();
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Connection-string storage uses Windows DPAPI and is available only on Windows.");
    }
}
