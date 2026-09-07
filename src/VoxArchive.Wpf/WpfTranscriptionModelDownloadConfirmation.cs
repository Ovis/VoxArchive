using System.Windows;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// 手動文字起こしで未取得モデルが必要になった場合に、WPFダイアログで取得可否を確認する
/// </summary>
public sealed class WpfTranscriptionModelDownloadConfirmation : ITranscriptionModelDownloadConfirmation
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        TranscriptionMissingModelInfo model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Application側はUI技術を知らないため、利用者の意思決定だけをこのadapterで取得する。
        // 実際のdownload所有権と再AdmissionはApplicationへ残し、Window寿命で処理が分断されないようにする。
        var result = ModernDialog.Show(
            $"文字起こしに必要なモデル「{model.DisplayName}」が取得されていません。\nモデルを取得して文字起こしを続行しますか？",
            "文字起こしモデル未取得",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        return Task.FromResult(result == MessageBoxResult.OK);
    }
}
