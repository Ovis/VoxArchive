using NUnit.Framework;
using VoxArchive.Domain;

namespace VoxArchive.IntegrationTests;

[TestFixture]
public sealed class AudioEditorAvailabilityTests
{
    [Test]
    public void CanOpen_Stoppedかつ非Export中ならTrue()
    {
        Assert.That(AudioEditorAvailability.CanOpen(RecordingState.Stopped, false), Is.True);
        Assert.That(AudioEditorAvailability.GetUnavailableReason(RecordingState.Stopped, false), Is.Null);
    }

    [TestCase(RecordingState.Starting)]
    [TestCase(RecordingState.Recording)]
    [TestCase(RecordingState.Pausing)]
    [TestCase(RecordingState.Paused)]
    [TestCase(RecordingState.Stopping)]
    [TestCase(RecordingState.Error)]
    public void CanOpen_録音処理状態ならFalse(RecordingState state)
    {
        Assert.That(AudioEditorAvailability.CanOpen(state, false), Is.False);
        Assert.That(AudioEditorAvailability.GetUnavailableReason(state, false), Does.Contain("録音"));
    }

    [Test]
    public void CanOpen_Export中ならFalse()
    {
        Assert.That(AudioEditorAvailability.CanOpen(RecordingState.Stopped, true), Is.False);
        Assert.That(AudioEditorAvailability.GetUnavailableReason(RecordingState.Stopped, true), Does.Contain("書き出し"));
    }
}
