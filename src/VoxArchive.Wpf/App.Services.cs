namespace VoxArchive.Wpf;

public partial class App
{
    /// <summary>
    /// アプリケーション起動時に構築したDIコンテナを、Windowから開始される補助ワークフローへ提供する
    /// </summary>
    /// <remarks>
    /// MainViewModelがLibraryWindowを手動生成する既存構造を維持したまま、文字起こしQueueなどのSingletonを
    /// 新しいLibraryワークフローから再利用するための橋渡しである。新しいSingletonを個別生成して状態を分断しない。
    /// </remarks>
    internal IServiceProvider Services
        => _host?.Services ?? throw new InvalidOperationException("アプリケーションサービスはまだ初期化されていません。");
}
