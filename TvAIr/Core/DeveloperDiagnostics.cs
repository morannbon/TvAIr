namespace TvAIr.Core;

/// <summary>
/// Developer Diagnostics の正本。
///
/// TvAIr は同一ソースから「開発者版」と「一般公開版」を生成する。
/// 開発者ログ・診断API・診断専用バッファ/スナップショットは開発のためだけに存在し、
/// 一般公開版では生成・保持・公開しない。ユーザー運用ログ(UserEventLogService)は別責務であり、
/// Developer Diagnostics を無効化しても一般公開版に残す。
///
/// 今後新しい開発診断を追加する場合も必ず TVAIR_DEVELOPER_DIAGNOSTICS 境界へ所属させる。
/// ログAddだけでなく、診断専用の計測・文字列構築・Queue・Task・購読・snapshot・ファイル・APIも同じ境界で切る。
/// Public buildでは「ログが見えない」だけでは不十分で、診断のためだけの処理・保持・外部露出を発生させない。
/// リリース時に個別削除・手作業で無効化する運用へ戻してはならない。
/// </summary>
internal static class DeveloperDiagnostics
{
#if TVAIR_DEVELOPER_DIAGNOSTICS
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif
}
