namespace VoxArchive.Domain;

/// <summary>
/// Audio Editorの編集状態に対するUndo/Redo履歴とClean Baselineを管理する
/// </summary>
/// <remarks>
/// 履歴には軽量な<see cref="AudioEditState"/>だけを保持し、PCMや波形は保持しない。
/// 書き出し成功時は履歴を破棄せず、その時点の状態だけを新しいClean Baselineとして記録する。
/// </remarks>
public sealed class AudioEditHistory
{
    private readonly Stack<AudioEditState> _undo = new();
    private readonly Stack<AudioEditState> _redo = new();
    private AudioEditState _cleanBaseline;

    /// <summary>
    /// 初期編集状態から履歴管理を開始する
    /// </summary>
    public AudioEditHistory(AudioEditState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        Current = initialState;
        _cleanBaseline = initialState;
    }

    /// <summary>
    /// 現在の編集状態
    /// </summary>
    public AudioEditState Current { get; private set; }

    /// <summary>
    /// Undo可能か
    /// </summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>
    /// Redo可能か
    /// </summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 最後に正常書き出しした状態または初期状態と異なるか
    /// </summary>
    public bool IsDirty => !Current.Equals(_cleanBaseline);

    /// <summary>
    /// 新しい編集状態を1操作として適用する
    /// </summary>
    /// <returns>状態が変化した場合はtrue</returns>
    public bool Apply(AudioEditState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (Current.Equals(next))
        {
            return false;
        }

        _undo.Push(Current);
        Current = next;
        _redo.Clear();
        return true;
    }

    /// <summary>
    /// 直前の編集状態へ戻す
    /// </summary>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(Current);
        Current = _undo.Pop();
        return true;
    }

    /// <summary>
    /// Undoした編集状態を再適用する
    /// </summary>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(Current);
        Current = _redo.Pop();
        return true;
    }

    /// <summary>
    /// 現在状態を新しいClean Baselineとして記録する
    /// </summary>
    public void MarkClean()
    {
        // 書き出し成功は編集操作ではないため、Undo/Redo履歴を消さずBaselineだけを更新する。
        _cleanBaseline = Current;
    }
}
