using System.IO;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Whisperによる文字起こしをエンジン契約へ接続し、結果をcanonical documentとして確定する
/// </summary>
/// <remarks>
/// 既存のWhisper認識・派生出力処理を維持したまま、JSONだけを必須の正本へ移行する。
/// 後続PRで認識結果と派生出力の責務をOrchestratorへ移すまで、既存サービスが生成したJSONをv2へ正規化する移行境界として機能する。
/// </remarks>
public sealed class WhisperTranscriptionEngine(
    WhisperTranscriptionService transcriptionService,
    TranscriptionDocumentStore documentStore) : ITranscriptionEngine
{
    /// <inheritdoc />
    public async Task<TranscriptionJobResult> TranscribeAsync(
        TranscriptionJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // canonical JSONはユーザーが追加出力としてJSONを選択しているかに関係なく必ず必要になる。
        // 既存Whisperサービスから認識済みsegmentsを安全に受け取る移行手段として、内部実行時だけJsonフラグを追加する。
        var executionRequest = request with
        {
            Options = request.Options with
            {
                TranscriptionOutputFormats = request.Options.TranscriptionOutputFormats | TranscriptionOutputFormats.Json
            }
        };

        var result = await transcriptionService.TranscribeAsync(executionRequest, cancellationToken);
        if (!result.Succeeded)
        {
            return result;
        }

        var documentPath = BuildDocumentPath(request.AudioFilePath, request.Options.TranscriptionModel);
        var generatedDocument = await documentStore.LoadAsync(documentPath, cancellationToken);
        var canonicalDocument = generatedDocument with
        {
            Source = new TranscriptionSource(Path.GetFileName(request.AudioFilePath)),
            Transcription = new TranscriptionIdentity
            {
                Engine = "whisper",
                Model = GetModelId(request.Options.TranscriptionModel),
                Options = new Dictionary<string, string?>
                {
                    ["executionMode"] = GetRequestedRuntimeId(request.Options.TranscriptionExecutionMode),
                    ["language"] = request.Options.TranscriptionLanguage
                }
            },
            Runtime = new TranscriptionRuntime
            {
                Requested = GetRequestedRuntimeId(request.Options.TranscriptionExecutionMode),
                // 現在のWhisper実装は実際にロードされたbackendを結果として返していないため、推測値は保存しない。
                Actual = null
            },
            CreatedAt = result.FinishedAt
        };

        // legacy形式で一時生成されたJSONを同じパスへv2として置き換える。
        // 読み込みだけでは旧ファイルを書き換えないDocumentStoreの方針とは分離し、新規文字起こし成功時だけ明示的に確定する。
        await documentStore.SaveAsync(documentPath, canonicalDocument, cancellationToken);

        return result with
        {
            GeneratedFiles = result.GeneratedFiles.Contains(documentPath, StringComparer.OrdinalIgnoreCase)
                ? result.GeneratedFiles
                : result.GeneratedFiles.Concat([documentPath]).ToArray()
        };
    }

    private static string BuildDocumentPath(string audioFilePath, TranscriptionModel model)
    {
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(audioFilePath);
        return Path.Combine(directory, $"{fileName}-{GetModelId(model)}.json");
    }

    private static string GetModelId(TranscriptionModel model) => model switch
    {
        TranscriptionModel.Tiny => "tiny",
        TranscriptionModel.Base => "base",
        TranscriptionModel.Small => "small",
        TranscriptionModel.Medium => "medium",
        TranscriptionModel.LargeV3 => "large-v3",
        _ => model.ToString().ToLowerInvariant()
    };

    private static string GetRequestedRuntimeId(TranscriptionExecutionMode mode) => mode switch
    {
        TranscriptionExecutionMode.CpuOnly => "cpu",
        _ => "auto"
    };
}
