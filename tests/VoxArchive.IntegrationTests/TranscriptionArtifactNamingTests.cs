using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;
using VoxArchive.Transcription.Whisper;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// 文字起こし基盤のリファクタリング後も既存artifact file nameを維持できることを検証する
/// </summary>
[TestFixture]
public sealed class TranscriptionArtifactNamingTests
{
    /// <summary>
    /// Whisperは従来どおりmodel IDだけをsuffixとして使用することを確認する
    /// </summary>
    [Test]
    public void WhisperArtifactNaming_PreservesExistingFileName()
    {
        var capability = new WhisperArtifactNamingCapability();
        var suffix = capability.BuildFileNameSuffix(new TranscriptionModelId("small"));

        var path = TranscriptionArtifactService.BuildDocumentPath(
            Path.Combine("recordings", "meeting.flac"),
            new TranscriptionEngineId("whisper"),
            new TranscriptionModelId("small"),
            suffix);

        Assert.That(Path.GetFileName(path), Is.EqualTo("meeting-small.json"));
    }

    /// <summary>
    /// ReazonSpeechは導入時からのengine-model suffixを維持することを確認する
    /// </summary>
    [Test]
    public void ReazonSpeechArtifactNaming_PreservesExistingFileName()
    {
        var capability = new ReazonSpeechArtifactNamingCapability();
        var suffix = capability.BuildFileNameSuffix(new TranscriptionModelId("ja"));

        var path = TranscriptionArtifactService.BuildDocumentPath(
            Path.Combine("recordings", "meeting.flac"),
            new TranscriptionEngineId("reazonspeech"),
            new TranscriptionModelId("ja"),
            suffix);

        Assert.That(Path.GetFileName(path), Is.EqualTo("meeting-reazonspeech-ja.json"));
    }

    /// <summary>
    /// Engine capabilityから不正なpath要素が返っても録音ディレクトリ外へ書き出さないことを確認する
    /// </summary>
    [Test]
    public void BuildDocumentPath_RejectsDirectoryTraversalSuffix()
    {
        Assert.Throws<ArgumentException>(() => TranscriptionArtifactService.BuildDocumentPath(
            Path.Combine("recordings", "meeting.flac"),
            new TranscriptionEngineId("test"),
            null,
            "../outside"));
    }
}
