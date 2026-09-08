using VoxArchive.Application;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// モデル管理操作で論理モデルIDを内部物理package IDへ正しく解決できることを確認する
/// </summary>
public sealed class TranscriptionModelOperationResolverServiceTests
{
    [TestCase("fp32", "ja-fp32")]
    [TestCase("int8", "ja-int8")]
    [TestCase("int8-fp32", "ja-int8-fp32")]
    public void ResolveModelId_ReazonSpeechUsesCurrentPrecision(string precision, string expected)
    {
        var service = new TranscriptionModelOperationResolverService(
            [new ReazonSpeechModelOperationCapability()]);
        var values = new Dictionary<string, string>
        {
            [ReazonSpeechAdvancedSettingsCapability.PrecisionKey] = precision
        };

        var actual = service.ResolveModelId("reazonspeech", "ja", values);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ResolveModelId_EngineWithoutCapabilityReturnsLogicalModelId()
    {
        var service = new TranscriptionModelOperationResolverService([]);

        var actual = service.ResolveModelId("whisper", "small", new Dictionary<string, string>());

        Assert.That(actual, Is.EqualTo("small"));
    }

    [Test]
    public void ResolveModelId_ReazonSpeechRejectsUnsupportedLogicalModel()
    {
        var service = new TranscriptionModelOperationResolverService(
            [new ReazonSpeechModelOperationCapability()]);
        var values = new Dictionary<string, string>
        {
            [ReazonSpeechAdvancedSettingsCapability.PrecisionKey] = "int8-fp32"
        };

        Assert.Throws<NotSupportedException>(
            () => service.ResolveModelId("reazonspeech", "ja-fp32", values));
    }
}
