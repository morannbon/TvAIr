using System.Security.Cryptography;
using System.Text;

namespace TvAIr.Core;

/// <summary>
/// Windows DPAPI（Data Protection API）を使用してパスワードを暗号化・復号するヘルパー。
/// DataProtectionScope.CurrentUser により、同一Windowsユーザーセッション内でのみ復号可能。
/// 暗号化データはBase64文字列としてiniファイルに保存する。
/// </summary>
public static class CredentialProtector
{
    // エントロピー（追加のランダム性）：アプリ固有の値で第三者による総当たりを困難にする
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TvAIr.TaskScheduler.v1");

    /// <summary>平文パスワードをDPAPIで暗号化してBase64文字列として返す。</summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var data      = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// Base64暗号化文字列を復号して平文パスワードを返す。
    /// 復号失敗（別ユーザー・別PC等）の場合はnullを返す。
    /// </summary>
    public static string? Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return null;
        try
        {
            var data      = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // 別ユーザー・別PC・データ破損等で復号不可
            return null;
        }
    }
}
