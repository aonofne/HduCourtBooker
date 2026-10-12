namespace HduCourtBooker.Core;

/// <summary>
/// token 的存取辅助。
///
/// 按用户要求，token 现在以**明文**保存在 data\config.json 中，便于查看、手工替换与排错，
/// 因此不再使用任何加密。
///
/// 兼容性：早期版本写入过 <c>dpapi:...</c> 密文。这里不做解密（避免引入 DPAPI 依赖），
/// 直接视为无效值返回空字符串，首次保存后配置里就会变成明文。
/// 配置里若出现这种旧值，说明需要重新获取一次 token。
/// </summary>
public static class SecretProtector
{
    private const string LegacyDpapiPrefix = "dpapi:";

    /// <summary>保存到配置里的形态：原文。</summary>
    public static string Protect(string plain) => plain ?? "";

    /// <summary>从配置里读出 token；旧版密文视为需要重新获取。</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (stored.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal)) return "";
        return stored;
    }

    /// <summary>判断配置里是否残留旧版加密值（供界面提示重新获取）。</summary>
    public static bool IsLegacyEncrypted(string stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(LegacyDpapiPrefix, StringComparison.Ordinal);
}
