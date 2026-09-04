using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace VoxArchive.Wpf;

/// <summary>
/// Engineに依存しない文字起こしモデルの選択・状態表示・管理操作UIを提供する
/// </summary>
/// <remarks>
/// Controlはモデル操作を所有しない。設定Windowを閉じても取得を継続できるよう、実処理はアプリケーションスコープの
/// 管理サービスへ委譲し、このControlは状態表示とユーザー操作の通知だけを担当する。
/// </remarks>
public partial class TranscriptionModelManagerControl : UserControl
{
    /// <summary>モデル管理Controlを初期化する</summary>
    public TranscriptionModelManagerControl()
    {
        InitializeComponent();
    }

    /// <summary>表示するモデル一覧</summary>
    public ObservableCollection<TranscriptionModelChoice> Models { get; } = [];

    /// <summary>選択中のモデルID</summary>
    public string? SelectedModelId
    {
        get => (string?)GetValue(SelectedModelIdProperty);
        set => SetValue(SelectedModelIdProperty, value);
    }

    /// <summary>選択中モデルの状態表示</summary>
    public string StatusText { get => (string)GetValue(StatusTextProperty); set => SetValue(StatusTextProperty, value); }

    /// <summary>状態の補足メッセージ</summary>
    public string MessageText { get => (string)GetValue(MessageTextProperty); set => SetValue(MessageTextProperty, value); }

    /// <summary>取得ボタンに表示する文言</summary>
    public string InstallButtonText { get => (string)GetValue(InstallButtonTextProperty); set => SetValue(InstallButtonTextProperty, value); }

    /// <summary>完全性確認を実行できるか</summary>
    public bool CanVerify { get => (bool)GetValue(CanVerifyProperty); set => SetValue(CanVerifyProperty, value); }

    /// <summary>取得・再取得を実行できるか</summary>
    public bool CanInstall { get => (bool)GetValue(CanInstallProperty); set => SetValue(CanInstallProperty, value); }

    /// <summary>削除を実行できるか</summary>
    public bool CanDelete { get => (bool)GetValue(CanDeleteProperty); set => SetValue(CanDeleteProperty, value); }

    /// <summary>取得進捗率</summary>
    public double ProgressPercent { get => (double)GetValue(ProgressPercentProperty); set => SetValue(ProgressPercentProperty, value); }

    /// <summary>転送量を含む進捗表示</summary>
    public string ProgressText { get => (string)GetValue(ProgressTextProperty); set => SetValue(ProgressTextProperty, value); }

    /// <summary>進捗表示の可視状態</summary>
    public Visibility ProgressVisibility { get => (Visibility)GetValue(ProgressVisibilityProperty); set => SetValue(ProgressVisibilityProperty, value); }

    public static readonly DependencyProperty SelectedModelIdProperty = DependencyProperty.Register(nameof(SelectedModelId), typeof(string), typeof(TranscriptionModelManagerControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(nameof(StatusText), typeof(string), typeof(TranscriptionModelManagerControl), new PropertyMetadata("未確認"));
    public static readonly DependencyProperty MessageTextProperty = DependencyProperty.Register(nameof(MessageText), typeof(string), typeof(TranscriptionModelManagerControl), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty InstallButtonTextProperty = DependencyProperty.Register(nameof(InstallButtonText), typeof(string), typeof(TranscriptionModelManagerControl), new PropertyMetadata("モデル取得"));
    public static readonly DependencyProperty CanVerifyProperty = DependencyProperty.Register(nameof(CanVerify), typeof(bool), typeof(TranscriptionModelManagerControl), new PropertyMetadata(true));
    public static readonly DependencyProperty CanInstallProperty = DependencyProperty.Register(nameof(CanInstall), typeof(bool), typeof(TranscriptionModelManagerControl), new PropertyMetadata(true));
    public static readonly DependencyProperty CanDeleteProperty = DependencyProperty.Register(nameof(CanDelete), typeof(bool), typeof(TranscriptionModelManagerControl), new PropertyMetadata(false));
    public static readonly DependencyProperty ProgressPercentProperty = DependencyProperty.Register(nameof(ProgressPercent), typeof(double), typeof(TranscriptionModelManagerControl), new PropertyMetadata(0d));
    public static readonly DependencyProperty ProgressTextProperty = DependencyProperty.Register(nameof(ProgressText), typeof(string), typeof(TranscriptionModelManagerControl), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ProgressVisibilityProperty = DependencyProperty.Register(nameof(ProgressVisibility), typeof(Visibility), typeof(TranscriptionModelManagerControl), new PropertyMetadata(Visibility.Collapsed));

    /// <summary>完全性確認が要求されたときに通知する</summary>
    public event EventHandler? VerifyRequested;

    /// <summary>取得・再取得・取得キャンセルが要求されたときに通知する</summary>
    public event EventHandler? InstallRequested;

    /// <summary>削除が要求されたときに通知する</summary>
    public event EventHandler? DeleteRequested;

    private void OnVerifyClick(object sender, RoutedEventArgs e) => VerifyRequested?.Invoke(this, EventArgs.Empty);
    private void OnInstallClick(object sender, RoutedEventArgs e) => InstallRequested?.Invoke(this, EventArgs.Empty);
    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// モデル選択ComboBoxへ表示する安定IDと表示名を保持する
/// </summary>
/// <param name="Id">永続化とProvider解決に利用するモデルID</param>
/// <param name="DisplayName">ユーザーへ表示する名称</param>
public sealed record TranscriptionModelChoice(string Id, string DisplayName);
