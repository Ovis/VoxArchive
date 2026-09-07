using VoxArchive.Application.Abstractions;
using VoxArchive.Domain;
using VoxArchive.Transcription;
using VoxArchive.Transcription.Abstractions;

namespace VoxArchive.Application;

/// <summary>
/// Engine registrationのcapabilityを使ってopaque settingsをUI編集可能な共通値へ変換する
/// </summary>
public sealed class TranscriptionEngineSettingsService(TranscriptionEngineRegistry engineRegistry)
    : ITranscriptionEngineSettingsService
{
    /// <inheritdoc />
    public TranscriptionEngineConfigurationInfo GetConfiguration(
        string engineId,
        TranscriptionEngineSettings settings)
    {
        var registration = engineRegistry.Get(new TranscriptionEngineId(engineId));
        var options = registration.SettingsProvider.Deserialize(settings.Settings, settings.SchemaVersion);
        var modelId = registration.ModelRequirementResolver?.ResolveRequiredModel(options).Value;
        var executionModeId = registration.ExecutionModeCapability?.GetSelectedMode(options);
        var executionModes = registration.ExecutionModeCapability?.GetAvailableModes()
            .Select(x => new TranscriptionExecutionModeInfo(x.Id, x.DisplayName))
            .ToArray()
            ?? [];
        return new TranscriptionEngineConfigurationInfo(modelId, executionModeId, executionModes);
    }

    /// <inheritdoc />
    public TranscriptionEngineSettings UpdateConfiguration(
        string engineId,
        TranscriptionEngineSettings settings,
        string? modelId,
        string? executionModeId)
    {
        var registration = engineRegistry.Get(new TranscriptionEngineId(engineId));
        var options = registration.SettingsProvider.Deserialize(settings.Settings, settings.SchemaVersion);

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            if (registration.ModelRequirementResolver is null)
            {
                throw new InvalidOperationException($"Engine '{engineId}' はモデル選択をサポートしていません。");
            }
            options = registration.ModelRequirementResolver.SelectModel(options, new TranscriptionModelId(modelId));
        }

        if (!string.IsNullOrWhiteSpace(executionModeId))
        {
            if (registration.ExecutionModeCapability is null)
            {
                throw new InvalidOperationException($"Engine '{engineId}' は実行方式の選択をサポートしていません。");
            }
            options = registration.ExecutionModeCapability.SelectMode(options, executionModeId);
        }

        var errors = registration.SettingsProvider.Validate(options);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Select(x => x.Message)));
        }

        return new TranscriptionEngineSettings
        {
            SchemaVersion = settings.SchemaVersion,
            Settings = registration.SettingsProvider.Serialize(options)
        };
    }
}
