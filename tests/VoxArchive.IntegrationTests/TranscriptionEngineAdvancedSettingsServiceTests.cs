using System.Text.Json;
using VoxArchive.Application;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;
using VoxArchive.Transcription.ReazonSpeech;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// Engine固有詳細設定がApplication層から安定ID/valueとして読み書きされ、Engine側validationを維持することを確認する
/// </summary>
public sealed class TranscriptionEngineAdvancedSettingsServiceTests
{
    private const string EngineId = "reazonspeech";

    [Test]
    public void GetValues_ProjectsReazonSpeechAdvancedSettingsWithoutExposingTypedOptions()
    {
        var service = CreateService();
        var settings = CreateSettings(
            precision: "int8-fp32",
            decodingMethod: "greedy_search",
            maxActivePaths: 4,
            cpuThreads: Math.Min(4, Environment.ProcessorCount));

        var values = service.GetValues(EngineId, settings);

        Assert.Multiple(() =>
        {
            Assert.That(values[ReazonSpeechAdvancedSettingsCapability.PrecisionKey], Is.EqualTo("int8-fp32"));
            Assert.That(values[ReazonSpeechAdvancedSettingsCapability.DecodingMethodKey], Is.EqualTo("greedy_search"));
            Assert.That(values[ReazonSpeechAdvancedSettingsCapability.MaxActivePathsKey], Is.EqualTo("4"));
            Assert.That(values[ReazonSpeechAdvancedSettingsCapability.CpuThreadsKey], Is.EqualTo(Math.Min(4, Environment.ProcessorCount).ToString()));
        });
    }

    [Test]
    public void UpdateValues_SerializesAdvancedSettingsAndPreservesLogicalModel()
    {
        var service = CreateService();
        var settings = CreateSettings(
            precision: "int8-fp32",
            decodingMethod: "greedy_search",
            maxActivePaths: 4,
            cpuThreads: 1);
        var values = new Dictionary<string, string>
        {
            [ReazonSpeechAdvancedSettingsCapability.PrecisionKey] = "fp32",
            [ReazonSpeechAdvancedSettingsCapability.DecodingMethodKey] = "modified_beam_search",
            [ReazonSpeechAdvancedSettingsCapability.MaxActivePathsKey] = "8",
            [ReazonSpeechAdvancedSettingsCapability.CpuThreadsKey] = "1"
        };

        var updated = service.UpdateValues(EngineId, settings, values);

        Assert.Multiple(() =>
        {
            Assert.That(updated.Settings.GetProperty("modelId").GetString(), Is.EqualTo("ja"));
            Assert.That(updated.Settings.GetProperty("precision").GetString(), Is.EqualTo("fp32"));
            Assert.That(updated.Settings.GetProperty("decodingMethod").GetString(), Is.EqualTo("modified_beam_search"));
            Assert.That(updated.Settings.GetProperty("maxActivePaths").GetInt32(), Is.EqualTo(8));
            Assert.That(updated.Settings.GetProperty("cpuThreads").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public void UpdateValues_WhenCpuThreadsIsOutOfRange_FailsWithoutClamping()
    {
        var service = CreateService();
        var settings = CreateSettings(
            precision: "int8-fp32",
            decodingMethod: "greedy_search",
            maxActivePaths: 4,
            cpuThreads: 1);
        var values = new Dictionary<string, string>
        {
            [ReazonSpeechAdvancedSettingsCapability.PrecisionKey] = "int8-fp32",
            [ReazonSpeechAdvancedSettingsCapability.DecodingMethodKey] = "greedy_search",
            [ReazonSpeechAdvancedSettingsCapability.MaxActivePathsKey] = "4",
            [ReazonSpeechAdvancedSettingsCapability.CpuThreadsKey] = (Environment.ProcessorCount + 1).ToString()
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => service.UpdateValues(EngineId, settings, values));

        Assert.That(exception!.Message, Does.Contain($"1～{Environment.ProcessorCount}"));
    }

    private static TranscriptionEngineAdvancedSettingsService CreateService()
    {
        var registration = new TranscriptionEngineRegistration(
            new FakeReazonSpeechEngine(),
            new ReazonSpeechEngineSettingsProvider());
        return new TranscriptionEngineAdvancedSettingsService(
            new TranscriptionEngineRegistry([registration]),
            [new ReazonSpeechAdvancedSettingsCapability()]);
    }

    private static TranscriptionEngineSettings CreateSettings(
        string precision,
        string decodingMethod,
        int maxActivePaths,
        int cpuThreads)
        => new()
        {
            SchemaVersion = 1,
            Settings = JsonSerializer.SerializeToElement(new
            {
                modelId = "ja",
                precision,
                decodingMethod,
                maxActivePaths,
                cpuThreads
            })
        };

    private sealed class FakeReazonSpeechEngine : ITranscriptionEngine
    {
        public TranscriptionEngineId Id => ReazonSpeechEngineIdentity.EngineId;
        public TranscriptionAudioRequirements AudioRequirements { get; } = new(16_000, 1, TranscriptionSampleFormat.Pcm16);

        public Task<TranscriptionEngineResult> TranscribeAsync(
            TranscriptionEngineRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TranscriptionEngineResult([]));
    }
}
