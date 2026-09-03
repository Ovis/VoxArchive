using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// ライブラリで選択中の録音に対する文字起こし結果一覧・選択状態・結果操作を管理する
/// </summary>
/// <remarks>
/// 結果一覧の走査と本文JSONの読み込みを分離し、録音選択時に全結果のsegmentsを読み込まない。
/// 本文は結果が選択された時点で初めてロードする。再出力と削除も選択中のcanonical documentを基準に行う。
/// </remarks>
public sealed class LibraryTranscriptionResultsState(
    TranscriptionResultDiscoveryService discoveryService,
    TranscriptionDocumentStore documentStore,
    TranscriptionExportService exportService) : INotifyPropertyChanged
{
    private LibraryTranscriptionResultItem? _selectedResult;
    private TranscriptionDocument? _selectedDocument;
    private string? _audioFilePath;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 現在の録音に対応する文字起こし結果を取得する
    /// </summary>
    public ObservableCollection<LibraryTranscriptionResultItem> Results { get; } = [];

    /// <summary>
    /// 現在選択されている文字起こし結果を取得する
    /// </summary>
    public LibraryTranscriptionResultItem? SelectedResult
    {
        get => _selectedResult;
        private set => SetField(ref _selectedResult, value);
    }

    /// <summary>
    /// 選択結果のcanonical documentを取得する
    /// </summary>
    public TranscriptionDocument? SelectedDocument
    {
        get => _selectedDocument;
        private set => SetField(ref _selectedDocument, value);
    }

    /// <summary>
    /// 結果件数をライブラリ上で表示するための文字列を取得する
    /// </summary>
    public string SummaryText => Results.Count == 0 ? "未文字起こし" : $"文字起こし {Results.Count}件";

    /// <summary>
    /// 録音を切り替え、対応する結果メタデータを再読み込みする
    /// </summary>
    /// <param name="audioFilePath">選択された録音ファイル。nullの場合は選択解除として扱う</param>
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

        var discovered = await discoveryService.DiscoverAsync(audioFilePath, cancellationToken);
        foreach (var metadata in discovered)
        {
            Results.Add(new LibraryTranscriptionResultItem(metadata));
        }
        OnPropertyChanged(nameof(SummaryText));

        // 現段階ではエンジン既定値がまだRequestへ一般化されていないため、最新結果を初期選択する。
        // Default Engine+Modelが導入された後は、既定結果が存在すればそちらを優先する規則へ差し替える。
        if (Results.Count > 0)
        {
            await SelectAsync(Results[0], cancellationToken);
        }
    }

    /// <summary>
    /// 指定結果を選択し、その結果の本文を遅延読み込みする
    /// </summary>
    public async Task SelectAsync(LibraryTranscriptionResultItem result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Results.Contains(result))
        {
            throw new InvalidOperationException("現在の録音に属さない文字起こし結果は選択できません。");
        }

        SelectedResult = result;
        SelectedDocument = await documentStore.LoadAsync(result.DocumentPath, cancellationToken);
    }

    /// <summary>
    /// 選択中のcanonical documentから指定形式の派生ファイルを再生成する
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportSelectedAsync(
        TranscriptionOutputFormats formats,
        CancellationToken cancellationToken = default)
    {
        var result = SelectedResult ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        var document = SelectedDocument ?? await documentStore.LoadAsync(result.DocumentPath, cancellationToken);
        return await exportService.WriteDerivedAsync(result.DocumentPath, document, formats, cancellationToken);
    }

    /// <summary>
    /// 選択中のcanonical JSONだけを削除し、派生TXT/SRT/VTTは残す
    /// </summary>
    /// <remarks>
    /// 派生ファイルはユーザーが手編集している可能性があるため、自動的には削除しない。
    /// </remarks>
    public async Task DeleteSelectedAsync(CancellationToken cancellationToken = default)
    {
        var result = SelectedResult ?? throw new InvalidOperationException("文字起こし結果が選択されていません。");
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(result.DocumentPath))
        {
            File.Delete(result.DocumentPath);
        }

        await RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// 現在の録音に対する結果一覧を再走査する
    /// </summary>
    /// <remarks>
    /// 文字起こし完了後に呼び出すことを想定し、同じdocument pathが残っていれば選択を維持する。
    /// </remarks>
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
