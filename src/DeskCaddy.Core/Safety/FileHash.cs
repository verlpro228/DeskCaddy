using System.Security.Cryptography;

namespace DeskCaddy.Core.Safety;

public static class FileHash
{
    /// <summary>
    /// 以共享读方式计算哈希，不会锁定文件、不会修改任何内容。
    /// </summary>
    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();

        return Convert.ToHexString(sha.ComputeHash(stream));
    }
}
