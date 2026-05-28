using System.Security.Cryptography;

namespace Launcher.Core.Common;

public static class Sha1
{
    public static string Compute(Stream stream)
    {
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Compute(fs);
    }

    public static bool VerifyFile(string path, string expectedHex) =>
        string.Equals(ComputeFile(path), expectedHex, StringComparison.OrdinalIgnoreCase);
}
