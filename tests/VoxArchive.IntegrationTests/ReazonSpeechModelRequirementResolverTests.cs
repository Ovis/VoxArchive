using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeechのprecisionごとに必要なモデルファイルが正しく選択されることを確認する
/// </summary>
public sealed class ReazonSpeechModelRequirementResolverTests
{
    private static readonly string[] AllModelFiles =
    [
        Path.Combine("models", "encoder-epoch-99-avg-1.onnx"),
        Path.Combine("models", "encoder-epoch-99-avg-1.int8.onnx"),
        Path.Combine("models", "decoder-epoch-99-avg-1.onnx"),
        Path.Combine("models", "decoder-epoch-99-avg-1.int8.onnx"),
        Path.Combine("models", "joiner-epoch-99-avg-1.onnx"),
        Path.Combine("models", "joiner-epoch-99-avg-1.int8.onnx"),
        Path.Combine("models", "tokens.txt")
    ];

    [TestCase(ReazonSpeechPrecision.Fp32,
        "encoder-epoch-99-avg-1.onnx",
        "decoder-epoch-99-avg-1.onnx",
        "joiner-epoch-99-avg-1.onnx")]
    [TestCase(ReazonSpeechPrecision.Int8,
        "encoder-epoch-99-avg-1.int8.onnx",
        "decoder-epoch-99-avg-1.int8.onnx",
        "joiner-epoch-99-avg-1.int8.onnx")]
    [TestCase(ReazonSpeechPrecision.Int8Fp32,
        "encoder-epoch-99-avg-1.int8.onnx",
        "decoder-epoch-99-avg-1.onnx",
        "joiner-epoch-99-avg-1.int8.onnx")]
    public void BindInstallation_SelectsFilesForConfiguredPrecision(
        ReazonSpeechPrecision precision,
        string expectedEncoder,
        string expectedDecoder,
        string expectedJoiner)
    {
        var resolver = new ReazonSpeechModelRequirementResolver();
        var options = new ReazonSpeechEngineOptions { Precision = precision };
        var installation = new TranscriptionModelInstallation(
            ReazonSpeechEngineIdentity.EngineId,
            options.ModelId,
            AllModelFiles);

        var bound = (ReazonSpeechEngineOptions)resolver.BindInstallation(options, installation);

        Assert.Multiple(() =>
        {
            Assert.That(Path.GetFileName(bound.EncoderPath), Is.EqualTo(expectedEncoder));
            Assert.That(Path.GetFileName(bound.DecoderPath), Is.EqualTo(expectedDecoder));
            Assert.That(Path.GetFileName(bound.JoinerPath), Is.EqualTo(expectedJoiner));
            Assert.That(Path.GetFileName(bound.TokensPath), Is.EqualTo("tokens.txt"));
        });
    }

    [Test]
    public void BindInstallation_WhenRequiredPrecisionFileIsMissing_ThrowsInsteadOfFallingBack()
    {
        var resolver = new ReazonSpeechModelRequirementResolver();
        var options = new ReazonSpeechEngineOptions { Precision = ReazonSpeechPrecision.Fp32 };
        var installation = new TranscriptionModelInstallation(
            ReazonSpeechEngineIdentity.EngineId,
            options.ModelId,
            AllModelFiles.Where(x => !x.EndsWith("encoder-epoch-99-avg-1.onnx", StringComparison.OrdinalIgnoreCase)).ToArray());

        Assert.Throws<InvalidDataException>(() => resolver.BindInstallation(options, installation));
    }
}
