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

        Assert.Multiple(() =>
        {
            Assert.That(options.OutputDirectory, Is.EqualTo("C:\\Recordings"));
            Assert.That(options.Transcription.Enabled, Is.False);
            Assert.That(options.Transcription.AutoAfterRecord, Is.True);
            Assert.That(options.Transcription.DiagnosticsLogEnabled, Is.True);
            Assert.That(options.Transcription.ToastNotificationEnabled, Is.False);
            Assert.That(options.Transcription.Whisper.ExecutionMode, Is.EqualTo(TranscriptionExecutionMode.CpuOnly));
            Assert.That(options.Transcription.Whisper.Model, Is.EqualTo(TranscriptionModel.Medium));
            Assert.That(options.Transcription.Whisper.Language, Is.EqualTo("en"));
            Assert.That(options.Transcription.OutputFormats, Is.EqualTo(TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Srt));
            Assert.That(options.Transcription.AutoPriority, Is.EqualTo(TranscriptionPriority.Low));
            Assert.That(options.Transcription.ManualPriority, Is.EqualTo(TranscriptionPriority.Normal));
            Assert.That(options.Transcription.DefaultEngine, Is.EqualTo("whisper"));
        });
    }

    /// <summary>
    /// 新形式を保存した際に旧フラット項目が再出力されず、Engine別設定だけが正本になることを確認する
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
                Whisper = new WhisperTranscriptionSettings
                {
                    Model = TranscriptionModel.LargeV3,
                    ExecutionMode = TranscriptionExecutionMode.Auto,
                    Language = "ja"
                },
                ReazonSpeech = new ReazonSpeechTranscriptionSettings { Model = "ja-en" },
                OutputFormats = TranscriptionOutputFormats.Txt | TranscriptionOutputFormats.Vtt
            }
        };

        await service.SaveRecordingOptionsAsync(options);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var root = document.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.TryGetProperty("Transcription", out var transcription), Is.True);
            Assert.That(transcription.GetProperty("DefaultEngine").GetString(), Is.EqualTo("whisper"));
            Assert.That(transcription.GetProperty("Whisper").GetProperty("Model").GetInt32(), Is.EqualTo((int)TranscriptionModel.LargeV3));
            Assert.That(transcription.GetProperty("ReazonSpeech").GetProperty("Model").GetString(), Is.EqualTo("ja-en"));
            Assert.That(root.TryGetProperty("TranscriptionModel", out _), Is.False);
            Assert.That(root.TryGetProperty("TranscriptionExecutionMode", out _), Is.False);
            Assert.That(root.TryGetProperty("TranscriptionEnabled", out _), Is.False);
        });
    }
}
