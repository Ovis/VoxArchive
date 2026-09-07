namespace VoxArchive.Domain;

/// <summary>
/// 旧設定画面XAMLが文字列ベースの実行モードへ移行するまで参照する互換用の値を提供する
/// </summary>
/// <remarks>
/// 文字起こしEngine固有の実行モードをDomainへ戻さないため、値は列挙型ではなく永続化と同じ安定文字列IDとして公開する。
/// SettingsWindow.xamlのTag参照を文字列へ変更できた時点で、この型は削除する。
/// </remarks>
[Obsolete("SettingsWindow.xamlの移行完了後に削除する互換型です。")]
public static class TranscriptionExecutionMode
{
    /// <summary>
    /// 利用可能な実行環境を自動選択する
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// CPU実行を明示的に要求する
    /// </summary>
    public const string CpuOnly = "cpu";
}
