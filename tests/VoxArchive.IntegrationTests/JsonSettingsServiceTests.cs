using System.Text.Json;
using VoxArchive.Domain;
using VoxArchive.Infrastructure;

namespace VoxArchive.IntegrationTests;

/// <summary>
/// JsonSettingsServiceの文字起こし設定移行と保存形式を検証する
/// </summary>
[TestFixture]
public sealed class JsonSettingsServiceTests
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

    /// <summary>
    /// 新規設定では共通UIの「指定なし」に対応する空の言語指定を既定値とすることを確認する
    /// </summary>
    [Test]
    public void NewRecordingOptions_DefaultsTranscriptionLanguageToUnspecified()
    {
        var options = new RecordingOptions();

        Assert.That(options.Transcription.PreferredLanguage, Is.Empty);
    }

    /// <summary>
    /// 旧settings.jsonのフラットな文字起こし設定が新しいEngine別構造へ移行されることを確認する
    /// </summary>
    [Test]
    public async Task LoadRecordingOptionsAsync_MigratesLegacyTranscriptionSettings()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
        {
          "OutputDirectory": "C:\\Recordings",
          "TranscriptionDiagnosticsLogEnabled": true,
          "TranscriptionEnabled": false,
          "AutoTranscriptionAfterRecord": true,
          "TranscriptionExecutionMode": 1,
          "TranscriptionModel": 3,
          "TranscriptionLanguage": "en",
          "TranscriptionOutputFormats": 3,
          "AutoTranscriptionPriority": 0,
          "ManualTranscriptionPriority": 1,
          "TranscriptionToastNotificationEnabled": false
        }
        """);

        var service = new JsonSettingsService(settingsPath);
        var options = await service.LoadRecordingOptionsAsync();
        var whisper = options.Transcription.Engines["whisper"].Settings;

        Assert.Multiple(() =>
        {
            Assert.That(options.OutputDirectory, Is.EqualTo("C:\\Recordings"));
            Assert.That(options.Transcription.Enabled, Is.False);
            Assert.That(options.Transcription.AutoAfterRecord, Is.True);
            Assert.That(options.Transcription.DiagnosticsLogEnabled, Is.True);
            Assert.That(options.Transcription.ToastNotificationEnabled, Is.False);
            Assert.That(whisper.GetProperty("executionMode").GetString(), Is.EqualTo("cpu"));
            Assert.That(whisper.GetProperty("modelId").GetString(), Is.EqualTo("medium"));
            Assert.That(options.Transcription.PreferredLanguage, Is.EqualTo("en"));
            Assert.That(options.Transcription.OutputFormats, Is.EqualTo(TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt));
            Assert.That(options.Transcription.AutoPriority, Is.EqualTo(TranscriptionPriority.Low));
            Assert.That(options.Transcription.ManualPriority, Is.EqualTo(TranscriptionPriority.Normal));
            Assert.That(options.Transcription.DefaultEngine, Is.EqualTo("whisper"));
        });
    }

    /// <summary>
    /// 新形式を保存した際に旧typed項目が再出力されず、Engine別settings blobだけが正本になることを確認する
    /// </summary>
    [Test]
    public async Task SaveRecordingOptionsAsync_WritesNestedTranscriptionSettingsOnly()
    {
        var settingsPath = Path.Combine(_tempDirectory, "settings.json");
        var service = new JsonSettingsService(settingsPath);
        var options = new RecordingOptions
        {
            Transcription = new TranscriptionSettings
            {
                Enabled = true,
                DefaultEngine = "whisper",
                PreferredLanguage = "ja",
                Engines = new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase)
                {
                    ["whisper"] = new()
                    {
                        SchemaVersion = 1,
                        Settings = JsonSerializer.SerializeToElement(new { modelId = "large-v3", executionMode = "auto" })
                    },
                    ["reazonspeech"] = new()
                    {
                        SchemaVersion = 1,
                        Settings = JsonSerializer.SerializeToElement(new { modelId = "ja-en" })
                    },
                    ["future-engine"] = new()
                    {
                        SchemaVersion = 7,
                        Settings = JsonSerializer.SerializeToElement(new { custom = "preserved" })
                    }
                },
                OutputFormats = TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Vtt
            }
        };

        await service.SaveRecordingOptionsAsync(options);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var root = document.RootElement;
        var transcription = root.GetProperty("Transcription");
        var engines = transcription.GetProperty("Engines");
        Assert.Multiple(() =>
        {
            Assert.That(transcription.GetProperty("DefaultEngine").GetString(), Is.EqualTo("whisper"));
            Assert.That(transcription.GetProperty("PreferredLanguage").GetString(), Is.EqualTo("ja"));
            Assert.That(engines.GetProperty("whisper").GetProperty("Settings").GetProperty("modelId").GetString(), Is.EqualTo("large-v3"));
            Assert.That(engines.GetProperty("reazonspeech").GetProperty("Settings").GetProperty("modelId").GetString(), Is.EqualTo("ja-en"));
            Assert.That(engines.GetProperty("future-engine").GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(7));
            Assert.That(engines.GetProperty("future-engine").GetProperty("Settings").GetProperty("custom").GetString(), Is.EqualTo("preserved"));
            Assert.That(transcription.TryGetProperty("Whisper", out _), Is.False);
            Assert.That(transcription.TryGetProperty("ReazonSpeech", out _), Is.False);
            Assert.That(root.TryGetProperty("TranscriptionModel", out _), Is.False);
            Assert.That(root.TryGetProperty("TranscriptionExecutionMode", out _), Is.False);
            Assert.That(root.TryGetProperty("TranscriptionEnabled", out _), Is.False);
        });
    }
}
