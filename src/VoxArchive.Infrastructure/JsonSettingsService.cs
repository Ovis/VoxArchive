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
        if (!File.Exists(settingsPath)) return NormalizeForLoad(new RecordingOptions(), null);
        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var options = JsonSerializer.Deserialize<RecordingOptions>(json, SerializerOptions) ?? new RecordingOptions();
            return NormalizeForLoad(options, document.RootElement);
        }
        catch (JsonException) { return NormalizeForLoad(new RecordingOptions(), null); }
        catch (IOException) { return NormalizeForLoad(new RecordingOptions(), null); }
    }

    /// <inheritdoc />
    public async Task SaveRecordingOptionsAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var normalized = NormalizeForSave(options);
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = settingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            // 旧Whisper/ReazonSpeech DTOはJsonIgnoreなので、migration後はEngine blob形式だけを書き出す。
            await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        if (File.Exists(settingsPath)) File.Replace(tempPath, settingsPath, null);
        else File.Move(tempPath, settingsPath);
    }

    private static RecordingOptions NormalizeForLoad(RecordingOptions options, JsonElement? rawRoot)
    {
        var migrated = rawRoot is { } root
            ? ResolveTranscriptionFromRaw(root, options.Transcription)
            : EnsureEngineSettings(options.Transcription);
        migrated = NormalizeCommon(migrated);
        migrated = AttachCompatibilityViews(migrated);
        return options with { Transcription = migrated };
    }

    private static RecordingOptions NormalizeForSave(RecordingOptions options)
    {
        // WPF/Applicationの通常call-siteはすでにEnginesを正本として更新する。
        // ここで旧typed互換DTOから再構築すると、未知Engine設定や画面で更新したblobを失うため行わない。
        var normalized = NormalizeCommon(EnsureEngineSettings(options.Transcription));
        return options with { Transcription = normalized };
    }

    private static TranscriptionSettings NormalizeCommon(TranscriptionSettings settings)
        => settings with
        {
            DefaultEngine = string.IsNullOrWhiteSpace(settings.DefaultEngine) ? "whisper" : settings.DefaultEngine.Trim().ToLowerInvariant(),
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
        // この知識を通常runtime pathへ残さず、ここで新Engine blobへ一度だけ変換する。
        var whisper = TryGetProperty(transcriptionElement, "whisper", out var w)
            ? JsonSerializer.Deserialize<LegacyWhisperSettings>(w.GetRawText(), SerializerOptions) ?? new LegacyWhisperSettings()
            : new LegacyWhisperSettings();
        var reazon = TryGetProperty(transcriptionElement, "reazonSpeech", out var r)
            ? JsonSerializer.Deserialize<LegacyReazonSettings>(r.GetRawText(), SerializerOptions) ?? new LegacyReazonSettings()
            : new LegacyReazonSettings();
        var preferredLanguage = TryGetProperty(transcriptionElement, "preferredLanguage", out var preferred)
                                && preferred.ValueKind == JsonValueKind.String
            ? preferred.GetString() ?? string.Empty
            : whisper.Language ?? string.Empty;
        return deserialized with
        {
            PreferredLanguage = preferredLanguage,
            Engines = CreateEngineSettings(
                whisper.Model ?? TranscriptionModel.Small,
                whisper.ExecutionMode ?? TranscriptionExecutionMode.Auto,
                reazon.Model ?? "ja")
        };
    }

    private static TranscriptionSettings BuildFromFlatLegacy(LegacyFlatTranscriptionOptions legacy)
    {
        var defaults = new TranscriptionSettings();
        var whisperModel = legacy.TranscriptionModel ?? TranscriptionModel.Small;
        var execution = legacy.TranscriptionExecutionMode ?? TranscriptionExecutionMode.Auto;
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
            Engines = CreateEngineSettings(whisperModel, execution, "ja")
        };
    }

    private static TranscriptionSettings EnsureEngineSettings(TranscriptionSettings settings)
    {
        if (settings.Engines.Count > 0) return settings;
#pragma warning disable CS0618
        return settings with
        {
            Engines = CreateEngineSettings(settings.Whisper.Model, settings.Whisper.ExecutionMode, settings.ReazonSpeech.Model),
            PreferredLanguage = string.IsNullOrWhiteSpace(settings.PreferredLanguage) ? settings.Whisper.Language : settings.PreferredLanguage
        };
#pragma warning restore CS0618
    }

    private static IReadOnlyDictionary<string, TranscriptionEngineSettings> CreateEngineSettings(
        TranscriptionModel model,
        TranscriptionExecutionMode executionMode,
        string reazonModel)
    {
#pragma warning disable CS0618
        var mode = executionMode switch
        {
            TranscriptionExecutionMode.CpuOnly => "cpu",
            TranscriptionExecutionMode.CudaPreferred => "auto",
            _ => "auto"
        };
#pragma warning restore CS0618
        var whisperModel = model switch
        {
            TranscriptionModel.Tiny => "tiny",
            TranscriptionModel.Base => "base",
            TranscriptionModel.Small => "small",
            TranscriptionModel.Medium => "medium",
            TranscriptionModel.LargeV3 => "large-v3",
            _ => "small"
        };
        return new Dictionary<string, TranscriptionEngineSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["whisper"] = new() { SchemaVersion = 1, Settings = JsonSerializer.SerializeToElement(new { modelId = whisperModel, executionMode = mode }) },
            ["reazonspeech"] = new() { SchemaVersion = 1, Settings = JsonSerializer.SerializeToElement(new { modelId = string.IsNullOrWhiteSpace(reazonModel) ? "ja" : reazonModel.Trim() }) }
        };
    }

    private static TranscriptionSettings AttachCompatibilityViews(TranscriptionSettings settings)
    {
#pragma warning disable CS0618
        return settings with { Whisper = BuildLegacyWhisperView(settings), ReazonSpeech = BuildLegacyReazonView(settings) };
#pragma warning restore CS0618
    }

#pragma warning disable CS0618
    private static WhisperTranscriptionSettings BuildLegacyWhisperView(TranscriptionSettings settings)
    {
        if (!settings.Engines.TryGetValue("whisper", out var engine)) return new WhisperTranscriptionSettings { Language = settings.PreferredLanguage };
        var model = ReadString(engine.Settings, "modelId") switch
        {
            "tiny" => TranscriptionModel.Tiny,
            "base" => TranscriptionModel.Base,
            "medium" => TranscriptionModel.Medium,
            "large-v3" => TranscriptionModel.LargeV3,
            _ => TranscriptionModel.Small
        };
        var execution = string.Equals(ReadString(engine.Settings, "executionMode"), "cpu", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionExecutionMode.CpuOnly
            : TranscriptionExecutionMode.Auto;
        return new WhisperTranscriptionSettings { Model = model, ExecutionMode = execution, Language = settings.PreferredLanguage };
    }

    private static ReazonSpeechTranscriptionSettings BuildLegacyReazonView(TranscriptionSettings settings)
        => new()
        {
            Model = settings.Engines.TryGetValue("reazonspeech", out var engine)
                ? ReadString(engine.Settings, "modelId") ?? "ja"
                : "ja"
        };
#pragma warning restore CS0618

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

    private sealed record LegacyWhisperSettings
    {
        public TranscriptionModel? Model { get; init; }
        public TranscriptionExecutionMode? ExecutionMode { get; init; }
        public string? Language { get; init; }
    }
    private sealed record LegacyReazonSettings { public string? Model { get; init; } }
    private sealed record LegacyFlatTranscriptionOptions
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
