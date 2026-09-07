using System.Text.Json;
using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;

namespace VoxArchive.Infrastructure;

/// <summary>
/// RecordingOptionsをJSONファイルへ永続化し、旧文字起こし設定を新しいEngine blob形式へ移行する
/// </summary>
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
            return NormalizeForLoad(new RecordingOptions(), null);
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var options = JsonSerializer.Deserialize<RecordingOptions>(json, SerializerOptions) ?? new RecordingOptions();
            return NormalizeForLoad(options, document.RootElement);
        }
        catch (JsonException)
        {
            return NormalizeForLoad(new RecordingOptions(), null);
        }
        catch (IOException)
        {
            return NormalizeForLoad(new RecordingOptions(), null);
        }
    }

    /// <inheritdoc />
    public async Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalized = NormalizeForSave(options);
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            // migration後はEngine blob形式だけを書き出す。旧形式を再生成しないことで、
            // DomainへEngine固有settingsを持ち込まず、未知Engineのblobもそのまま保持する。
            await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions, cancellationToken);
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

    private static RecordingOptions NormalizeForLoad(RecordingOptions options, JsonElement? rawRoot)
    {
        var migrated = rawRoot is { } root
            ? ResolveTranscriptionFromRaw(root, options.Transcription)
            : EnsureEngineSettings(options.Transcription);

        return options with { Transcription = NormalizeCommon(migrated) };
    }

    private static RecordingOptions NormalizeForSave(RecordingOptions options)
    {
        // Enginesは通常call-siteの正本である。保存時に既知Engineだけで再構築すると、
        // 将来追加されたEngineや外部Engineのsettingsを失うため既存dictionaryを保持する。
        var normalized = NormalizeCommon(EnsureEngineSettings(options.Transcription));
        return options with { Transcription = normalized };
    }

    private static TranscriptionSettings NormalizeCommon(TranscriptionSettings settings)
        => settings with
        {
            DefaultEngine = string.IsNullOrWhiteSpace(settings.DefaultEngine)
                ? "whisper"
                : settings.DefaultEngine.Trim().ToLowerInvariant(),
            // 空文字は「指定なし」という有効な共通intentなのでjaへ補完しない。
            PreferredLanguage = settings.PreferredLanguage?.Trim() ?? string.Empty
        };

    private static TranscriptionSettings ResolveTranscriptionFromRaw(JsonElement root, TranscriptionSettings deserialized)
    {
        if (!TryGetProperty(root, nameof(RecordingOptions.Transcription), out var transcriptionElement)
            || transcriptionElement.ValueKind != JsonValueKind.Object)
        {
            var legacy = JsonSerializer.Deserialize<LegacyFlatTranscriptionOptions>(root.GetRawText(), SerializerOptions)
                         ?? new LegacyFlatTranscriptionOptions();
            return BuildFromFlatLegacy(legacy);
        }

        if (TryGetProperty(transcriptionElement, nameof(TranscriptionSettings.Engines), out var engines)
            && engines.ValueKind == JsonValueKind.Object)
        {
            return EnsureEngineSettings(deserialized);
        }

        // PR #25までの中間形式はtranscription.whisper/reazonSpeechとしてtyped settingsを保存していた。
        // 旧enumはmigration専用private型で受け、現在のDomain契約へ再導入しない。
        var whisper = TryGetProperty(transcriptionElement, "whisper", out var whisperElement)
            ? JsonSerializer.Deserialize<LegacyWhisperSettings>(whisperElement.GetRawText(), SerializerOptions)
              ?? new LegacyWhisperSettings()
            : new LegacyWhisperSettings();
        var reazon = TryGetProperty(transcriptionElement, "reazonSpeech", out var reazonElement)
            ? JsonSerializer.Deserialize<LegacyReazonSettings>(reazonElement.GetRawText(), SerializerOptions)
              ?? new LegacyReazonSettings()
            : new LegacyReazonSettings();

        var preferredLanguage = TryGetProperty(transcriptionElement, "preferredLanguage", out var preferred)
                                && preferred.ValueKind == JsonValueKind.String
            ? preferred.GetString() ?? string.Empty
            : whisper.Language ?? string.Empty;

        return deserialized with
        {
            PreferredLanguage = preferredLanguage,
            Engines = CreateEngineSettings(
                whisper.Model ?? LegacyTranscriptionModel.Small,
                whisper.ExecutionMode ?? LegacyTranscriptionExecutionMode.Auto,
                reazon.Model ?? "ja")
        };
    }

    private static TranscriptionSettings BuildFromFlatLegacy(LegacyFlatTranscriptionOptions legacy)
    {
        var defaults = new TranscriptionSettings();
        return defaults with
        {
            Enabled = legacy.TranscriptionEnabled ?? defaults.Enabled,
            AutoAfterRecord = legacy.AutoTranscriptionAfterRecord ?? defaults.AutoAfterRecord,
            PreferredLanguage = legacy.TranscriptionLanguage ?? string.Empty,
            OutputFormats = legacy.TranscriptionOutputFormats ?? defaults.OutputFormats,
            AutoPriority = legacy.AutoTranscriptionPriority ?? defaults.AutoPriority,
            ManualPriority = legacy.ManualTranscriptionPriority ?? defaults.ManualPriority,
            ToastNotificationEnabled = legacy.TranscriptionToastNotificationEnabled ?? defaults.ToastNotificationEnabled,
            DiagnosticsLogEnabled = legacy.TranscriptionDiagnosticsLogEnabled ?? defaults.DiagnosticsLogEnabled,
            Engines = CreateEngineSettings(
                legacy.TranscriptionModel ?? LegacyTranscriptionModel.Small,
                legacy.TranscriptionExecutionMode ?? LegacyTranscriptionExecutionMode.Auto,
                "ja")
        };
    }

    private static TranscriptionSettings EnsureEngineSettings(TranscriptionSettings settings)
    {
        if (settings.Engines.Count > 0)
        {
            return settings;
        }

        // 新規settingsでは既定Engine設定だけを生成する。ここで用いる値は永続化schemaの既定値であり、
        // Domain型としてWhisper固有enumを公開する必要はない。
        return settings with
        {
            Engines = CreateEngineSettings(
                LegacyTranscriptionModel.Small,
                LegacyTranscriptionExecutionMode.Auto,
                "ja")
        };
    }

    private static IReadOnlyDictionary<string, TranscriptionEngineSettings> CreateEngineSettings(
        LegacyTranscriptionModel model,
        LegacyTranscriptionExecutionMode executionMode,
        string reazonModel)
    {
        var mode = executionMode == LegacyTranscriptionExecutionMode.CpuOnly ? "cpu" : "auto";
        var whisperModel = model switch
        {
            LegacyTranscriptionModel.Tiny => "tiny",
            LegacyTranscriptionModel.Base => "base",
            LegacyTranscriptionModel.Small => "small",
            LegacyTranscriptionModel.Medium => "medium",
            LegacyTranscriptionModel.LargeV3 => "large-v3",
            _ => "small"
        };

        return new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["whisper"] = new()
            {
                SchemaVersion = 1,
                Settings = JsonSerializer.SerializeToElement(new { modelId = whisperModel, executionMode = mode })
            },
            ["reazonspeech"] = new()
            {
                SchemaVersion = 1,
                Settings = JsonSerializer.SerializeToElement(new
                {
                    modelId = string.IsNullOrWhiteSpace(reazonModel) ? "ja" : reazonModel.Trim()
                })
            }
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 旧settings.jsonのWhisper model値を読み取るためだけのmigration enum
    /// </summary>
    private enum LegacyTranscriptionModel
    {
        Tiny = 0,
        Base = 1,
        Small = 2,
        Medium = 3,
        LargeV3 = 4
    }

    /// <summary>
    /// 旧settings.jsonのruntime要求値を読み取るためだけのmigration enum
    /// </summary>
    private enum LegacyTranscriptionExecutionMode
    {
        Auto = 0,
        CpuOnly = 1,
        CudaPreferred = 2
    }

    private sealed record LegacyWhisperSettings
    {
        public LegacyTranscriptionModel? Model { get; init; }
        public LegacyTranscriptionExecutionMode? ExecutionMode { get; init; }
        public string? Language { get; init; }
    }

    private sealed record LegacyReazonSettings
    {
        public string? Model { get; init; }
    }

    private sealed record LegacyFlatTranscriptionOptions
    {
        public bool? TranscriptionDiagnosticsLogEnabled { get; init; }
        public bool? TranscriptionEnabled { get; init; }
        public bool? AutoTranscriptionAfterRecord { get; init; }
        public LegacyTranscriptionExecutionMode? TranscriptionExecutionMode { get; init; }
        public LegacyTranscriptionModel? TranscriptionModel { get; init; }
        public string? TranscriptionLanguage { get; init; }
        public TranscriptionOutputFormats? TranscriptionOutputFormats { get; init; }
        public TranscriptionPriority? AutoTranscriptionPriority { get; init; }
        public TranscriptionPriority? ManualTranscriptionPriority { get; init; }
        public bool? TranscriptionToastNotificationEnabled { get; init; }
    }
}
