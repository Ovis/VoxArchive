using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

/// <summary>
/// RecordingOptionsをJSONファイルへ永続化する
/// </summary>
/// <remarks>
/// 文字起こし設定はEngine別の入れ子構造へ移行したが、旧settings.jsonは破棄せず読み込み時に変換する。
/// 移行はメモリ上だけで行い、利用者が次に設定を保存するまではファイルを書き換えない。
/// </remarks>
public sealed class JsonSettingsService(string settingsPath) : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <inheritdoc />
    public async Task<RecordingOptions> LoadRecordingOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return new RecordingOptions();
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            var options = JsonSerializer.Deserialize<RecordingOptions>(json, SerializerOptions)
                ?? new RecordingOptions();

            using var document = JsonDocument.Parse(json);
            if (!HasProperty(document.RootElement, nameof(RecordingOptions.Transcription)))
            {
                // 旧形式では文字起こし設定がRecordingOptions直下に並んでいた。
                // RecordingOptions側の互換アクセサーはJsonIgnoreとしているため、ここで一度だけ新構造へ投影する。
                var legacy = JsonSerializer.Deserialize<LegacyTranscriptionOptions>(json, SerializerOptions)
                    ?? new LegacyTranscriptionOptions();
                options = options with { Transcription = BuildMigratedTranscriptionSettings(legacy) };
            }

            options = NormalizeTranscriptionSettings(options);
            return options;
        }
        catch (JsonException)
        {
            return new RecordingOptions();
        }
        catch (IOException)
        {
            return new RecordingOptions();
        }
    }

    /// <inheritdoc />
    public async Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, NormalizeTranscriptionSettings(options), SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (File.Exists(settingsPath))
        {
            File.Replace(tempPath, settingsPath, null);
        }
        else
        {
            File.Move(tempPath, settingsPath);
        }
    }

    private static RecordingOptions NormalizeTranscriptionSettings(RecordingOptions options)
    {
        var transcription = options.Transcription;
        var whisper = transcription.Whisper;

#pragma warning disable CS0618 // CudaPreferredは旧設定を読み込むためだけに残している。
        if (whisper.ExecutionMode == TranscriptionExecutionMode.CudaPreferred)
        {
            whisper = whisper with { ExecutionMode = TranscriptionExecutionMode.Auto };
        }
#pragma warning restore CS0618

        var defaultEngine = string.IsNullOrWhiteSpace(transcription.DefaultEngine)
            ? TranscriptionEngineId.Whisper.Value
            : transcription.DefaultEngine.Trim().ToLowerInvariant();
        var language = string.IsNullOrWhiteSpace(whisper.Language) ? "ja" : whisper.Language.Trim();

        return options with
        {
            Transcription = transcription with
            {
                DefaultEngine = defaultEngine,
                Whisper = whisper with { Language = language }
            }
        };
    }

    private static TranscriptionSettings BuildMigratedTranscriptionSettings(LegacyTranscriptionOptions legacy)
    {
        var defaults = new TranscriptionSettings();
        return defaults with
        {
            Enabled = legacy.TranscriptionEnabled ?? defaults.Enabled,
            AutoAfterRecord = legacy.AutoTranscriptionAfterRecord ?? defaults.AutoAfterRecord,
            OutputFormats = legacy.TranscriptionOutputFormats ?? defaults.OutputFormats,
            AutoPriority = legacy.AutoTranscriptionPriority ?? defaults.AutoPriority,
            ManualPriority = legacy.ManualTranscriptionPriority ?? defaults.ManualPriority,
            ToastNotificationEnabled = legacy.TranscriptionToastNotificationEnabled ?? defaults.ToastNotificationEnabled,
            DiagnosticsLogEnabled = legacy.TranscriptionDiagnosticsLogEnabled ?? defaults.DiagnosticsLogEnabled,
            Whisper = defaults.Whisper with
            {
                ExecutionMode = legacy.TranscriptionExecutionMode ?? defaults.Whisper.ExecutionMode,
                Model = legacy.TranscriptionModel ?? defaults.Whisper.Model,
                Language = legacy.TranscriptionLanguage ?? defaults.Whisper.Language
            }
        };
    }

    private static bool HasProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return element.EnumerateObject().Any(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 入れ子構造導入前のsettings.jsonに存在した文字起こし項目だけを受け取る移行用DTO
    /// </summary>
    private sealed record LegacyTranscriptionOptions
    {
        public bool? TranscriptionDiagnosticsLogEnabled { get; init; }
        public bool? TranscriptionEnabled { get; init; }
        public bool? AutoTranscriptionAfterRecord { get; init; }
        public TranscriptionExecutionMode? TranscriptionExecutionMode { get; init; }
        public TranscriptionModel? TranscriptionModel { get; init; }
        public string? TranscriptionLanguage { get; init; }
        public TranscriptionOutputFormats? TranscriptionOutputFormats { get; init; }
        public TranscriptionPriority? AutoTranscriptionPriority { get; init; }
        public TranscriptionPriority? ManualTranscriptionPriority { get; init; }
        public bool? TranscriptionToastNotificationEnabled { get; init; }
    }
}
