using System.IO;
using System.Net.Http;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Whisperモデルの既存配置規則を維持しながら、共通モデルProvider境界を提供する
/// </summary>
/// <remarks>
/// 既存ユーザーのダウンロード済みモデルをそのまま利用するため、この段階では保存先を
/// <c>%LOCALAPPDATA%\VoxArchive\whisper\models</c> から移動しない。
/// </remarks>
public sealed class WhisperModelStore : ITranscriptionModelProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly string _modelsDirectory;

    /// <summary>Whisperモデルストアを初期化する</summary>
    /// <param name="modelsDirectory">テスト等で既定保存先を上書きする場合のディレクトリ</param>
    public WhisperModelStore(string? modelsDirectory = null)
    {
        _modelsDirectory = string.IsNullOrWhiteSpace(modelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive", "whisper", "models")
            : modelsDirectory;
        Directory.CreateDirectory(_modelsDirectory);
    }

    /// <inheritdoc />
    public TranscriptionEngineId EngineId => TranscriptionEngineId.Whisper;

    /// <summary>既存Whisperモデルの保存先を取得する</summary>
    public string ModelsDirectory => _modelsDirectory;

    /// <summary>指定したWhisperモデルの物理パスを取得する</summary>
    public string GetModelPath(TranscriptionModel model)
    {
        return Path.Combine(_modelsDirectory, GetModelFileName(model));
    }

    /// <summary>指定したWhisperモデルが配置済みか確認する</summary>
    public bool IsInstalled(TranscriptionModel model)
    {
        return File.Exists(GetModelPath(model));
    }

    /// <inheritdoc />
    public bool IsInstalled(TranscriptionModelId modelId)
    {
        return IsInstalled(ParseModelId(modelId));
    }

    /// <summary>指定したWhisperモデルを削除する</summary>
    public Task DeleteAsync(TranscriptionModel model, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetModelPath(model);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(TranscriptionModelId modelId, CancellationToken cancellationToken = default)
    {
        return DeleteAsync(ParseModelId(modelId), cancellationToken);
    }

    /// <summary>
    /// 指定したWhisperモデルを既存保存先へダウンロードする
    /// </summary>
    public async Task<string> DownloadAsync(TranscriptionModel model, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDirectory);
        var destinationPath = GetModelPath(model);
        if (File.Exists(destinationPath))
        {
            return destinationPath;
        }

        var tmpPath = destinationPath + ".download";
        if (File.Exists(tmpPath))
        {
            File.Delete(tmpPath);
        }

        try
        {
            var url = BuildModelDownloadUrl(model);
            using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(tmpPath))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            File.Move(tmpPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        catch
        {
            // 中断したファイルを配置済みモデルと誤認しないよう、一時ファイルは失敗時に必ず除去する。
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<TranscriptionModelInstallation> InstallAsync(
        TranscriptionModelId modelId,
        CancellationToken cancellationToken = default)
    {
        var model = ParseModelId(modelId);
        var path = await DownloadAsync(model, cancellationToken);
        return new TranscriptionModelInstallation(EngineId, modelId, [path]);
    }

    /// <inheritdoc />
    public TranscriptionModelInstallation GetInstallation(TranscriptionModelId modelId)
    {
        var model = ParseModelId(modelId);
        var path = GetModelPath(model);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Whisperモデル '{modelId}' は配置されていません。");
        }

        return new TranscriptionModelInstallation(EngineId, modelId, [path]);
    }

    /// <summary>Whisperモデルに対応するファイル名を取得する</summary>
    public static string GetModelFileName(TranscriptionModel model)
    {
        return model switch
        {
            TranscriptionModel.Tiny => "ggml-tiny.bin",
            TranscriptionModel.Base => "ggml-base.bin",
            TranscriptionModel.Small => "ggml-small.bin",
            TranscriptionModel.Medium => "ggml-medium.bin",
            TranscriptionModel.LargeV3 => "ggml-large-v3.bin",
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "未対応のWhisperモデルです。")
        };
    }

    private static TranscriptionModel ParseModelId(TranscriptionModelId modelId)
    {
        return modelId.Value switch
        {
            "tiny" => TranscriptionModel.Tiny,
            "base" => TranscriptionModel.Base,
            "small" => TranscriptionModel.Small,
            "medium" => TranscriptionModel.Medium,
            "large-v3" => TranscriptionModel.LargeV3,
            _ => throw new NotSupportedException($"Whisperモデル '{modelId}' はサポートされていません。")
        };
    }

    private static string BuildModelDownloadUrl(TranscriptionModel model)
    {
        var file = GetModelFileName(model);
        return $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}?download=true";
    }
}
