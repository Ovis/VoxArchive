using System.Windows;
using VoxArchive.Application.Abstractions;

namespace VoxArchive.Wpf;

/// <summary>
/// Applicationから要求されたモデル取得進捗表示をWPF Windowとして生成する
/// </summary>
public sealed class WpfTranscriptionModelDownloadProgressPresentation : ITranscriptionModelDownloadProgressPresentation
{
    /// <inheritdoc />
    public ITranscriptionModelDownloadProgressSession Show(
        TranscriptionMissingModelInfo model,
        Action cancelAction)
    {
        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF Applicationを取得できません。");

        TranscriptionModelDownloadProgressWindow? window = null;
        void ShowWindow()
        {
            var owner = application.Windows
                .OfType<Window>()
                .FirstOrDefault(candidate => candidate.IsActive)
                ?? application.MainWindow;

            window = new TranscriptionModelDownloadProgressWindow(model, cancelAction)
            {
                Owner = owner
            };
            window.Show();
        }

        if (application.Dispatcher.CheckAccess())
        {
            ShowWindow();
        }
        else
        {
            application.Dispatcher.Invoke(ShowWindow);
        }

        return window ?? throw new InvalidOperationException("モデル取得進捗Windowを生成できませんでした。");
    }
}
