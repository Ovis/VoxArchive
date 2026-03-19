using System.IO;
using System.Net.Http;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class WhisperModelStore
{
    private static readonly HttpClient HttpClient = new();
    private readonly string _modelsDirectory;

    public WhisperModelStore(string? modelsDirectory = null)
    {
        _modelsDirectory = string.IsNullOrWhiteSpace(modelsDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxArchive", "whisper", "models")
            : modelsDirectory;
        Directory.CreateDirectory(_modelsDirectory);
    }

    public string ModelsDirectory => _modelsDirectory;

    public string GetModelPath(TranscriptionModel model)
    {
        return Path.Combine(_modelsDirectory, GetModelFileName(model));
    }

    public bool IsInstalled(TranscriptionModel model)
    {
        return File.Exists(GetModelPath(model));
    }

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
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }

            throw;
        }
    }

    public static string GetModelFileName(TranscriptionModel model)
    {
        return model switch
        {
            TranscriptionModel.Tiny => "ggml-tiny.bin",
            TranscriptionModel.Base => "ggml-base.bin",
            TranscriptionModel.Small => "ggml-small.bin",
            TranscriptionModel.Medium => "ggml-medium.bin",
            TranscriptionModel.LargeV3 => "ggml-large-v3.bin",
            _ => "ggml-small.bin"
        };
    }

    private static string BuildModelDownloadUrl(TranscriptionModel model)
    {
        var file = GetModelFileName(model);
        return $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}?download=true";
    }
}



