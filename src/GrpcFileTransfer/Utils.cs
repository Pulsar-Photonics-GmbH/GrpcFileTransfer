using System.Security.Cryptography;

namespace Pulsar.GrpcFileTransfer;

public static class Utils
{
    public static string CalculateMD5(string filename)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filename);
        var hash = md5.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public const int ChunkSize = 1024 * 1024; // 1MB
}