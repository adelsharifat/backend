using System.Security.Cryptography;
using System.Text;

namespace CMIS.Core.Security;

public static class Md5Helper
{
    public static string ComputeHash(string input)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        // Convert to lower‑case hex
        var sb = new StringBuilder(32);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}