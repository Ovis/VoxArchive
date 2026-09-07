using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeechのJob snapshotがsherpa-onnx設定へそのまま反映されることを確認する
/// </summary>
public sealed class ReazonSpeechRecognizerConfigTests
{
    [TestCase(ReazonSpeechDecodingMethod.GreedySearch, "greedy_search")]
    [TestCase(ReazonSpeechDecodingMethod.ModifiedBeamSearch, "modified_beam_search")]
    public void CreateRecognizerConfig_AppliesDecodingMethod(
        ReazonSpeechDecodingMethod decodingMethod,
        string expected)
    {
        var options = CreateOptions() with { DecodingMethod = decodingMethod };

        var config = ReazonSpeechRecognizer.CreateRecognizerConfig(options, diagnosticsEnabled: false);

        Assert.That(config.DecodingMethod, Is.EqualTo(expected));
    }

    [Test]
    public void CreateRecognizerConfig_AppliesCpuThreadsAndMaxActivePathsWithoutClamping()
    {
        var options = CreateOptions() with
        {
            CpuThreads = 3,
            MaxActivePaths = 7
        };

        var config = ReazonSpeechRecognizer.CreateRecognizerConfig(options, diagnosticsEnabled: false);

        Assert.Multiple(() =>
        {
            Assert.That(config.ModelConfig.NumThreads, Is.EqualTo(3));
            Assert.That(config.MaxActivePaths, Is.EqualTo(7));
            Assert.That(config.ModelConfig.Provider, Is.EqualTo("cpu"));
        });
    }

    [Test]
    public void CreateRecognizerConfig_AppliesDiagnosticsFlagToNativeDebug()
    {
        var options = CreateOptions();

        var disabled = ReazonSpeechRecognizer.CreateRecognizerConfig(options, diagnosticsEnabled: false);
        var enabled = ReazonSpeechRecognizer.CreateRecognizerConfig(options, diagnosticsEnabled: true);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.ModelConfig.Debug, Is.Zero);
            Assert.That(enabled.ModelConfig.Debug, Is.EqualTo(1));
        });
    }

    private static ReazonSpeechEngineOptions CreateOptions()
        => new()
        {
            EncoderPath = "encoder.onnx",
            DecoderPath = "decoder.onnx",
            JoinerPath = "joiner.onnx",
            TokensPath = "tokens.txt"
        };
}
