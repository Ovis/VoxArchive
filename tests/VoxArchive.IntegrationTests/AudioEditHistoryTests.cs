using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioEditHistoryTests
{
    [Test]
    public void ApplyUndoRedo_TracksStateAndDirtyFlag()
    {
        var initial = State();
        var changed = initial.AddCutRange(Range(2, 4));
        var history = new AudioEditHistory(initial);

        Assert.That(history.Apply(changed), Is.True);
        Assert.That(history.IsDirty, Is.True);
        Assert.That(history.CanUndo, Is.True);

        Assert.That(history.Undo(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(history.Current, Is.EqualTo(initial));
            Assert.That(history.IsDirty, Is.False);
            Assert.That(history.CanRedo, Is.True);
        });

        Assert.That(history.Redo(), Is.True);
        Assert.That(history.Current, Is.EqualTo(changed));
        Assert.That(history.IsDirty, Is.True);
    }

    [Test]
    public void ApplySameState_DoesNotCreateUndoEntry()
    {
        var initial = State();
        var history = new AudioEditHistory(initial);

        Assert.Multiple(() =>
        {
            Assert.That(history.Apply(State()), Is.False);
            Assert.That(history.CanUndo, Is.False);
            Assert.That(history.IsDirty, Is.False);
        });
    }

    [Test]
    public void NewEditAfterUndo_ClearsRedoHistory()
    {
        var initial = State();
        var first = initial.AddCutRange(Range(2, 4));
        var history = new AudioEditHistory(initial);
        history.Apply(first);
        history.Undo();

        history.Apply(initial.WithChannelState(0, new AudioChannelEditState(3d)));

        Assert.That(history.CanRedo, Is.False);
    }

    [Test]
    public void MarkClean_UpdatesBaselineWithoutClearingUndoHistory()
    {
        var initial = State();
        var exported = initial.AddCutRange(Range(2, 4));
        var history = new AudioEditHistory(initial);
        history.Apply(exported);

        history.MarkClean();

        Assert.Multiple(() =>
        {
            Assert.That(history.IsDirty, Is.False);
            Assert.That(history.CanUndo, Is.True);
        });

        history.Undo();
        Assert.That(history.IsDirty, Is.True);

        history.Redo();
        Assert.That(history.IsDirty, Is.False);
    }

    [Test]
    public void AutomaticCutMerge_UndoReturnsToStateBeforeAddition()
    {
        var initial = new AudioEditState(TimeSpan.FromSeconds(20), 1, [Range(2, 5)]);
        var merged = initial.AddCutRange(Range(5, 8));
        var history = new AudioEditHistory(initial);
        history.Apply(merged);

        history.Undo();

        Assert.That(history.Current.CutRanges, Is.EqualTo(new[] { Range(2, 5) }));
    }

    private static AudioEditState State()
        => new(TimeSpan.FromSeconds(10), 2);

    private static AudioCutRange Range(double startSeconds, double endSeconds)
        => new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));
}
