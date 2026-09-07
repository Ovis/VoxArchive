using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで選択中の録音に対する文字起こし結果一覧・選択状態・結果操作を管理する
/// </summary>
/// <remarks>
/// canonical schemaやファイル走査はApplication Use Caseへ委譲する。
/// WPFはUIの選択状態だけを保持し、Transcription Coreの永続化型やserviceへ直接依存しない。
/// </remarks>
public sealed class LibraryTranscriptionResultsState(
    ITranscriptionApplicationService transcriptionApplicationService) : INotifyPropertyChanged
{
    private LibraryTranscriptionResultItem? _selectedResult;
    private TranscriptionResultDocumentInfo? _selectedDocument;
    private string? _audioFilePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>現在の録音に対応する文字起こし結果を取得する</summary>
    public ObservableCollection<LibraryTranscriptionResultItem> Results { get; } = [];

    /// <summary>現在選択されている文字起こし結果を取得する</summary>
    public LibraryTranscriptionResultItem? SelectedResult
    {
        get => _selectedResult;
        private set => SetField(ref _selectedResult, value);
    }

    /// <summary>選択結果のUI向けcanonical documentを取得する</summary>
    public TranscriptionResultDocumentInfo? SelectedDocument
    {
        get => _selectedDocument;
        private set => SetField(ref _selectedDocument, value);
    }

    /// <summary>結果件数をライブラリ上で表示するための文字列を取得する</summary>
    public string SummaryText => Results.Count == 0 ? "未文字起こし" : $"文字起こし {Results.Count}件";

    /// <summary>
    /// 録音を切り替え、対応する結果メタデータを再読み込みする
    /// </summary>
    public async Task LoadForRecordingAsync(string? audioFilePath, CancellationToken cancellationToken = default)
    {
        _audioFilePath = audioFilePath;
        Results.Clear();
        SelectedResult = null;
        SelectedDocument = null;

        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            OnPropertyChanged(nameof(SummaryText));
            return;
        }

        var discovered = await transcriptionApplicationService.DiscoverResultsAsync(audioFilePath, cancellationToken);
        foreach (var result in discovered)
        {
            Results.Add(new LibraryTranscriptionResultItem(result));
        }
        OnPropertyChanged(nameof(SummaryText));

        if (Results.Count > 0)
        {
            // Application側でCreatedAt降順に返すため、先頭が最新のcanonical resultとなる。
            await SelectAsync(Results[0], cancellationToken);
        }
    }

    /// <summary>指定結果を選択し、その結果の本文を遅延読み込みする</summary>
    public async Task SelectAsync(LibraryTranscriptionResultItem result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Results.Contains(result))
        {
            throw new InvalidOperationException("現在の録音に属さない文字起こし結果は選択できません。");
        }

        SelectedResult = result;
        SelectedDocument = await transcriptionApplicationService.LoadResultAsync(result.DocumentPath, cancellationToken);
    }

    /// <summary>選択中のcanonical documentから指定形式の派生ファイルを再生成する</summary>
    public Task<IReadOnlyList<string>> ExportSelectedAsync(
        TranscriptionOutputFormats formats,
        CancellationToken cancellationToken = default)
    {
        var result = SelectedResult ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        return transcriptionApplicationService.ExportResultAsync(result.DocumentPath, formats, cancellationToken);
    }

    /// <summary>
    /// 選択中のcanonical JSONだけを削除し、派生TXT/SRT/VTTは残す
    /// </summary>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        var result = SelectedResult ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        await transcriptionApplicationService.DeleteResultAsync(result.DocumentPath, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    /// <summary>現在の録音に対する結果一覧を再走査する</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var selectedPath = SelectedResult?.DocumentPath;
        var audioFilePath = _audioFilePath;
        await LoadForRecordingAsync(audioFilePath, cancellationToken);

        if (selectedPath is null) return;
        var previous = Results.FirstOrDefault(x => string.Equals(x.DocumentPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        if (previous is not null && !ReferenceEquals(previous, SelectedResult))
        {
            await SelectAsync(previous, cancellationToken);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
