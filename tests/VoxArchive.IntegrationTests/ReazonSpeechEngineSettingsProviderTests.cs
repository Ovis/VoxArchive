using System.Text.Json;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// ReazonSpeech固有設定の後方互換deserialize、永続化、preflight validation条件を確認する
/// </summary>
public sealed class ReazonSpeechEngineSettingsProviderTests
{
    [Test]
    public void Deserialize_LegacyModelOnlySettings_UsesCurrentDefaultRecognitionProfile()
    {
        var settings = JsonSerializer.SerializeToElement(new { modelId = "ja" });
        var sut = new ReazonSpeechEngineSettingsProvider();

        var options = (ReazonSpeechEngineOptions)sut.Deserialize(settings, schemaVersion: 1);

        Assert.Multiple(() =>
        {
            Assert.That(options.ModelId.Value, Is.EqualTo("ja"));
            Assert.That(options.Precision, Is.EqualTo(ReazonSpeechPrecision.Int8Fp32));
            Assert.That(options.DecodingMethod, Is.EqualTo(ReazonSpeechDecodingMethod.GreedySearch));
            Assert.That(options.MaxActivePaths, Is.EqualTo(4));
            Assert.That(options.CpuThreads, Is.EqualTo(Math.Min(4, Environment.ProcessorCount)));
        });
    }

    [Test]
    public void SerializeAndDeserialize_PreservesRecognitionSettings()
    {
        var sut = new ReazonSpeechEngineSettingsProvider();
        var source = new ReazonSpeechEngineOptions
        {
            Precision = ReazonSpeechPrecision.Fp32,
            DecodingMethod = ReazonSpeechDecodingMethod.ModifiedBeamSearch,
            MaxActivePaths = 7,
            CpuThreads = 1
        };

        var serialized = sut.Serialize(source);
        var restored = (ReazonSpeechEngineOptions)sut.Deserialize(serialized, schemaVersion: 1);

        Assert.Multiple(() =>
        {
            Assert.That(serialized.GetProperty("precision").GetString(), Is.EqualTo("fp32"));
            Assert.That(serialized.GetProperty("decodingMethod").GetString(), Is.EqualTo("modified_beam_search"));
            Assert.That(restored.Precision, Is.EqualTo(source.Precision));
            Assert.That(restored.DecodingMethod, Is.EqualTo(source.DecodingMethod));
            Assert.That(restored.MaxActivePaths, Is.EqualTo(source.MaxActivePaths));
            Assert.That(restored.CpuThreads, Is.EqualTo(source.CpuThreads));
        });
    }

    [Test]
    public void Validate_InvalidPersistedValues_AreReportedWithoutClamping()
    {
        var settings = JsonSerializer.SerializeToElement(new
        {
            modelId = "ja",
            precision = "future-precision",
            decodingMethod = "future-decoder",
            maxActivePaths = 0,
            cpuThreads = 0
        });
        var sut = new ReazonSpeechEngineSettingsProvider();
        var options = (ReazonSpeechEngineOptions)sut.Deserialize(settings, schemaVersion: 1);

        var errors = sut.Validate(options);

        Assert.Multiple(() =>
        {
            Assert.That(errors.Select(x => x.Code), Does.Contain("reazonspeech.precision.invalid"));
            Assert.That(errors.Select(x => x.Code), Does.Contain("reazonspeech.decoding.invalid"));
            Assert.That(errors.Select(x => x.Code), Does.Contain("reazonspeech.cpuThreads.invalid"));
            Assert.That(options.CpuThreads, Is.Zero);
        });
    }

    [Test]
    public void Validate_ModifiedBeamSearchRequiresPositiveMaxActivePaths()
    {
        var sut = new ReazonSpeechEngineSettingsProvider();
        var options = new ReazonSpeechEngineOptions
        {
            DecodingMethod = ReazonSpeechDecodingMethod.ModifiedBeamSearch,
            MaxActivePaths = 0,
            CpuThreads = 1
        };

        var errors = sut.Validate(options);

        Assert.That(errors.Select(x => x.Code), Does.Contain("reazonspeech.maxActivePaths.invalid"));
    }

    [Test]
    public void Validate_GreedySearchDoesNotUseMaxActivePaths()
    {
        var sut = new ReazonSpeechEngineSettingsProvider();
        var options = new ReazonSpeechEngineOptions
        {
            DecodingMethod = ReazonSpeechDecodingMethod.GreedySearch,
            MaxActivePaths = 0,
            CpuThreads = 1
        };

        var errors = sut.Validate(options);

        Assert.That(errors.Select(x => x.Code), Does.Not.Contain("reazonspeech.maxActivePaths.invalid"));
    }
}
