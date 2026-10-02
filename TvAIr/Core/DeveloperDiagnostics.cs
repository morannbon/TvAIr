namespace TvAIr.Core;

/// <summary>
/// Developer Diagnostics の正本。
///
/// TvAIr は同一ソースから「開発者版」と「一般公開版」を生成する。
/// 開発者ログ・診断API・診断専用バッファ/スナップショットは開発のためだけに存在し、
/// 一般公開版では生成・保持・公開しない。ユーザー運用ログ(UserEventLogService)は別責務であり、
/// Developer Diagnostics を無効化しても一般公開版に残す。
///
/// 開発診断は TVAIR_DEVELOPER_DIAGNOSTICS 境界にまとめ、一般公開版では診断専用処理を生成しない。
/// </summary>
internal static class DeveloperDiagnostics
{
#if TVAIR_DEVELOPER_DIAGNOSTICS
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif
}
