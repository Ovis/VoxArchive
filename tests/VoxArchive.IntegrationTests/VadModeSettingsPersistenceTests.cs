using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// VAD方式設定の既定値とJSON永続化の互換性を確認する
/// </summary>
public sealed class VadModeSettingsPersistenceTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "VoxArchive.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Test]
    public void NewSettings_DefaultsToSilero()
    {
        Assert.That(new SileroVadSettings().Mode, Is.EqualTo(SpeechRegionDetectorMode.Silero));
    }

    [Test]
    public async Task LoadRecordingOptionsAsync_WhenLegacySileroSettingsHaveNoMode_DefaultsToSilero()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
        {
          "Transcription": {
            "DefaultEngine": "whisper",
            "Engines": {},
            "SileroVad": {
              "Threshold": 0.61,
              "MinimumSpeechDurationMilliseconds": 120,
              "MinimumSilenceDurationMilliseconds": 550,
              "PrePaddingMilliseconds": 320,
              "PostPaddingMilliseconds": 210
            }
          }
        }
        """);

        var options = await new JsonSettingsService(settingsPath).LoadRecordingOptionsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(options.Transcription.SileroVad.Mode, Is.EqualTo(SpeechRegionDetectorMode.Silero));
            Assert.That(options.Transcription.SileroVad.Threshold, Is.EqualTo(0.61d));
        });
    }

    [Test]
    public async Task SaveAndLoadRecordingOptionsAsync_PreservesVolumeBasedModeAndSileroValues()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var service = new JsonSettingsService(settingsPath);
        var expected = new SileroVadSettings
        {
            Mode = SpeechRegionDetectorMode.VolumeBased,
            Threshold = 0.63d,
            MinimumSpeechDurationMilliseconds = 140,
            MinimumSilenceDurationMilliseconds = 620,
            PrePaddingMilliseconds = 350,
            PostPaddingMilliseconds = 240
        };
        var options = new RecordingOptions
        {
            Transcription = new TranscriptionSettings
            {
                SileroVad = expected
            }
        };

        await service.SaveRecordingOptionsAsync(options);
        var loaded = await service.LoadRecordingOptionsAsync();

        Assert.That(loaded.Transcription.SileroVad, Is.EqualTo(expected));
    }
}
