using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

/// <summary>
/// Whisper.netによる文字起こし処理を実行する
/// </summary>
/// <remarks>
/// この段階ではWhisper実行の制御自体は本サービスに残し、音声準備・VAD・話者判定・出力生成だけを
/// エンジン非依存のサービスへ分離する。後続のEngine/Orchestrator導入時にWhisper固有部分をさらに縮小する。
/// </remarks>
public sealed class WhisperTranscriptionService(
    WhisperModelStore modelStore,
    TranscriptionAudioPreparationService audioPreparationService,
    TranscriptionSpeechRegionDetector speechRegionDetector,
    TranscriptionSpeakerLabelService speakerLabelService,
    TranscriptionExportService exportService)
{
    private const double VadMergeGapMilliseconds = 300d;

    /// <summary>
    /// 現在のWhisper実行環境と選択モデルの利用可否を確認する
    /// </summary>
    public WhisperEnvironmentStatus CheckEnvironment(RecordingOptions options)
    {
        try
        {
            var runtimeAvailable = TryGetWhisperFactoryType(out _);
            var modelInstalled = modelStore.IsInstalled(options.TranscriptionModel);
            var cudaRuntimeAvailable = TryProbeCudaRuntimeForSettings(out var cudaRuntimeDetail);
            var cudaDriverAvailable = TryProbeCudaDriverForSettings(out var cudaDriverDetail);
            var cudaAvailable = cudaRuntimeAvailable && cudaDriverAvailable;
            var runtimeMessage = runtimeAvailable ? "Whisper.net ランタイムを検出しました。" : "Whisper.net ランタイムを検出できませんでした。";
            var modelMessage = modelInstalled
                ? $"モデル '{WhisperModelStore.GetModelFileName(options.TranscriptionModel)}' は配置済みです。"
                : $"モデル '{WhisperModelStore.GetModelFileName(options.TranscriptionModel)}' は未配置です。";
            var cudaMessage = cudaAvailable
                ? $"CUDA available (runtime: {cudaRuntimeDetail}, driver: {cudaDriverDetail})"
                : $"CUDA unavailable (runtime: {cudaRuntimeDetail}, driver: {cudaDriverDetail})";
            var detail = runtimeAvailable && modelInstalled
                ? "文字起こし実行の前提条件を満たしています。"
                : "設定画面のモデル管理/依存関係を確認してください。";

            if (options.TranscriptionExecutionMode == TranscriptionExecutionMode.CudaPreferred && !cudaAvailable)
            {
                detail += " CudaPreferred が選択されていますが、現在は CUDA を使用できません。CPU にフォールバックします。";
            }

            return new WhisperEnvironmentStatus(runtimeAvailable, modelInstalled, runtimeMessage, modelMessage, cudaAvailable, cudaMessage, detail);
        }
        catch (Exception ex)
        {
            return new WhisperEnvironmentStatus(false, false, "環境チェック中に例外が発生しました。", "モデル状態を判定できませんでした。", false, "CUDA 判定中に例外が発生しました。", ex.Message);
        }
    }

    /// <summary>
    /// Whisperで文字起こしし、既存形式の出力ファイルを生成する
    /// </summary>
    public async Task<TranscriptionJobResult> TranscribeAsync(TranscriptionJobRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        try
        {
            if (!File.Exists(request.AudioFilePath)) return Fail("対象ファイルが見つかりません。", started);
            if (request.Options.TranscriptionOutputFormats == TranscriptionOutputFormats.None) return Fail("出力形式が選択されていません。", started);

            var modelPath = modelStore.GetModelPath(request.Options.TranscriptionModel);
            if (!File.Exists(modelPath)) return Fail("モデルが未配置です。設定画面からモデルをダウンロードしてください。", started);
            if (!TryGetWhisperFactoryType(out var factoryType)) return Fail("Whisper.net ランタイムが利用できません。依存ライブラリを確認してください。", started);

            var segments = await ExecuteWhisperAsync(factoryType!, modelPath, request, cancellationToken);
            var labeledSegments = await Task.Run(() => speakerLabelService.Apply(request.AudioFilePath, segments, cancellationToken), cancellationToken);
            var generated = await exportService.WriteAsync(request.AudioFilePath, request.Options.TranscriptionModel, request.Options.TranscriptionOutputFormats, labeledSegments, cancellationToken);
            return new TranscriptionJobResult(true, "文字起こしが完了しました。", generated, started, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return Fail($"文字起こしに失敗しました: {ex.Message}", started);
        }
    }

    /// <summary>
    /// 現在の命名規則で生成対象となる出力ファイルパスを返す
    /// </summary>
    public static IReadOnlyList<string> BuildOutputPaths(string audioFilePath, TranscriptionModel model, TranscriptionOutputFormats formats)
        => new TranscriptionExportService().BuildOutputPaths(audioFilePath, model, formats);

    private async Task<IReadOnlyList<TranscribedSegment>> ExecuteWhisperAsync(Type factoryType, string modelPath, TranscriptionJobRequest request, CancellationToken cancellationToken)
    {
        var (fromPath, fromPathWithOptions) = FindFactoryFromPathMethods(factoryType);
        if (fromPath is null && fromPathWithOptions is null) throw new InvalidOperationException("WhisperFactory.FromPath was not found.");

        object? factory = null;
        object? processor = null;
        try
        {
            var factoryOptions = CreateFactoryOptions(factoryType.Assembly, request.Options.TranscriptionExecutionMode);
            factory = fromPathWithOptions is not null && factoryOptions is not null
                ? fromPathWithOptions.Invoke(null, [modelPath, factoryOptions])
                : fromPath?.Invoke(null, [modelPath]);
            if (factory is null) throw new InvalidOperationException("WhisperFactory initialization failed.");

            var createBuilder = factory.GetType().GetMethod("CreateBuilder", Type.EmptyTypes) ?? throw new InvalidOperationException("CreateBuilder was not found.");
            var builder = createBuilder.Invoke(factory, null) ?? throw new InvalidOperationException("Builder creation failed.");
            var language = string.IsNullOrWhiteSpace(request.Options.TranscriptionLanguage) ? "auto" : request.Options.TranscriptionLanguage.Trim();
            if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var withLanguage = FindStringInstanceMethod(builder.GetType(), "WithLanguage");
                if (withLanguage is not null) builder = withLanguage.Invoke(builder, [language]) ?? builder;
            }

            var build = builder.GetType().GetMethod("Build", Type.EmptyTypes) ?? throw new InvalidOperationException("Builder.Build was not found.");
            processor = build.Invoke(builder, null) ?? throw new InvalidOperationException("Processor creation failed.");
            var processAsync = FindProcessAsyncMethod(processor.GetType()) ?? throw new InvalidOperationException("Processor.ProcessAsync was not found.");

            await using var preparedAudio = await audioPreparationService.PrepareAsync(
                request.AudioFilePath,
                request.Options.DefaultSpeakerPlaybackGainDb,
                request.Options.DefaultMicPlaybackGainDb,
                cancellationToken);
            var speechRegions = await speechRegionDetector.DetectAsync(preparedAudio.WaveFilePath, cancellationToken);
            if (speechRegions.Count == 0) return Array.Empty<TranscribedSegment>();

            var collected = new List<TranscribedSegment>();
            foreach (var region in speechRegions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentWavePath = CreateTemporarySegmentWavePath();
                try
                {
                    WriteMonoWaveSegment(preparedAudio.WaveFilePath, segmentWavePath, region);
                    await using var segmentStream = File.OpenRead(segmentWavePath);
                    var result = processAsync.Invoke(processor, BuildProcessArgs(processAsync, segmentStream, cancellationToken))
                        ?? throw new InvalidOperationException("ProcessAsync returned null.");
                    var resolved = await UnwrapAwaitableAsync(result, cancellationToken)
                        ?? throw new InvalidOperationException("ProcessAsync resolved to null.");
                    var segments = await ReadSegmentsAsync(resolved, cancellationToken);
                    if (segments.Count > 0) collected.AddRange(OffsetSegments(segments, region.Start));
                }
                finally
                {
                    TryDeleteFile(segmentWavePath);
                }
            }

            return MergeAdjacentSegments(collected);
        }
        finally
        {
            await DisposeUnknownAsync(processor);
            await DisposeUnknownAsync(factory);
        }
    }

    private static object? CreateFactoryOptions(Assembly whisperAssembly, TranscriptionExecutionMode mode)
    {
        bool? requestedUseGpu = mode switch
        {
            TranscriptionExecutionMode.CpuOnly => false,
            TranscriptionExecutionMode.CudaPreferred => true,
            _ => null
        };
        var optionsType = whisperAssembly.GetType("Whisper.net.WhisperFactoryOptions");
        if (optionsType is null) return null;
        var options = Activator.CreateInstance(optionsType);
        if (options is null) return null;
        if (requestedUseGpu.HasValue)
        {
            var useGpuProperty = optionsType.GetProperty("UseGpu", BindingFlags.Public | BindingFlags.Instance);
            if (useGpuProperty?.CanWrite == true) useGpuProperty.SetValue(options, requestedUseGpu.Value);
        }
        return options;
    }

    private static bool TryGetWhisperFactoryType(out Type? factoryType)
    {
        factoryType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            factoryType = assembly.GetTypes().FirstOrDefault(t => t.Name == "WhisperFactory");
            if (factoryType is not null) return true;
        }
        try
        {
            var loaded = Assembly.Load("Whisper.net");
            factoryType = loaded.GetTypes().FirstOrDefault(t => t.Name == "WhisperFactory");
            return factoryType is not null;
        }
        catch { return false; }
    }

    private static bool TryProbeCudaRuntimeForSettings(out string detail)
    {
        try
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", arch, "ggml-cuda-whisper.dll"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "win-x64", "ggml-cuda-whisper.dll")
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(found))
            {
                detail = $"native runtime file found: {Path.GetFileName(found)}";
                return true;
            }
            detail = "cuda runtime assets not found";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"cuda runtime probe failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryProbeCudaDriverForSettings(out string detail)
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (File.Exists(Path.Combine(windows, "System32", "nvcuda.dll")))
            {
                detail = "nvcuda.dll を検出(System32)";
                return true;
            }
            if (File.Exists(Path.Combine(windows, "SysWOW64", "nvcuda.dll")))
            {
                detail = "nvcuda.dll を検出(SysWOW64)";
                return true;
            }
            detail = "nvcuda.dll を検出できません";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"nvcuda.dll 判定失敗: {ex.Message}";
            return false;
        }
    }

    private static void WriteMonoWaveSegment(string sourceWavePath, string segmentWavePath, SpeechRegion region)
    {
        using var reader = new AudioFileReader(sourceWavePath);
        var segmentProvider = new OffsetSampleProvider(reader) { SkipOver = region.Start, Take = region.Duration };
        WaveFileWriter.CreateWaveFile16(segmentWavePath, segmentProvider);
    }

    private static IReadOnlyList<TranscribedSegment> OffsetSegments(IReadOnlyList<TranscribedSegment> segments, TimeSpan offset)
        => segments.Select(x => x with { Start = x.Start + offset, End = x.End + offset }).ToArray();

    private static IReadOnlyList<TranscribedSegment> MergeAdjacentSegments(IReadOnlyList<TranscribedSegment> segments)
    {
        if (segments.Count <= 1) return segments;
        var ordered = segments.OrderBy(x => x.Start).ToList();
        var merged = new List<TranscribedSegment>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];
            if ((current.Start - last.End).TotalMilliseconds <= VadMergeGapMilliseconds
                && string.Equals(last.SpeakerLabel, current.SpeakerLabel, StringComparison.OrdinalIgnoreCase))
            {
                merged[^1] = last with
                {
                    End = current.End > last.End ? current.End : last.End,
                    Text = MergeSegmentText(last.Text, current.Text)
                };
            }
            else
            {
                merged.Add(current);
            }
        }
        return merged;
    }

    private static string MergeSegmentText(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        return $"{left} {right}";
    }

    private static string CreateTemporarySegmentWavePath() => Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-seg-{Guid.NewGuid():N}.wav");

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try { File.Delete(path); }
        catch
        {
            // GUID名の一時ファイルなので、削除失敗を文字起こし結果の失敗へ波及させない。
        }
    }

    private static (MethodInfo? FromPath, MethodInfo? FromPathWithOptions) FindFactoryFromPathMethods(Type factoryType)
    {
        MethodInfo? fromPath = null;
        MethodInfo? fromPathWithOptions = null;
        foreach (var method in factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, "FromPath", StringComparison.Ordinal)) continue;
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string)) fromPath = method;
            else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string)
                     && string.Equals(parameters[1].ParameterType.Name, "WhisperFactoryOptions", StringComparison.Ordinal)) fromPathWithOptions = method;
        }
        return (fromPath, fromPathWithOptions);
    }

    private static MethodInfo? FindStringInstanceMethod(Type type, string methodName)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));

    private static MethodInfo? FindProcessAsyncMethod(Type processorType)
        => processorType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ProcessAsync" && m.GetParameters().Length >= 1 && typeof(Stream).IsAssignableFrom(m.GetParameters()[0].ParameterType));

    private static object?[] BuildProcessArgs(MethodInfo processAsync, Stream stream, CancellationToken cancellationToken)
    {
        var parameters = processAsync.GetParameters();
        if (parameters.Length == 1) return [stream];
        var args = new object?[parameters.Length];
        args[0] = stream;
        for (var i = 1; i < parameters.Length; i++)
        {
            args[i] = parameters[i].ParameterType == typeof(CancellationToken)
                ? cancellationToken
                : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
        }
        return args;
    }

    private static async Task<IReadOnlyList<TranscribedSegment>> ReadSegmentsAsync(object source, CancellationToken cancellationToken)
    {
        if (await TryReadSegmentsFromAsyncEnumerableAsync(source, cancellationToken) is { } asyncSegments) return asyncSegments;
        if (TryReadSegmentsFromEnumerable(source) is { } syncSegments) return syncSegments;
        throw new InvalidOperationException($"文字起こし結果の列挙型に対応していません: {source.GetType().FullName}");
    }

    private static async Task<IReadOnlyList<TranscribedSegment>?> TryReadSegmentsFromAsyncEnumerableAsync(object source, CancellationToken cancellationToken)
    {
        var asyncEnumerableInterface = source.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        if (asyncEnumerableInterface is null) return null;
        var getAsyncEnumerator = asyncEnumerableInterface.GetMethod("GetAsyncEnumerator");
        if (getAsyncEnumerator is null) return null;
        var enumArgs = getAsyncEnumerator.GetParameters().Length == 1 ? [cancellationToken] : Array.Empty<object?>();
        var enumerator = getAsyncEnumerator.Invoke(source, enumArgs) ?? throw new InvalidOperationException("IAsyncEnumerable の列挙取得に失敗しました。");
        var asyncEnumeratorInterface = enumerator.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>))
            ?? throw new InvalidOperationException("IAsyncEnumerator インターフェイス取得に失敗しました。");
        var moveNextAsync = asyncEnumeratorInterface.GetMethod("MoveNextAsync") ?? throw new InvalidOperationException("MoveNextAsync が見つかりません。");
        var currentProperty = asyncEnumeratorInterface.GetProperty("Current") ?? throw new InvalidOperationException("Current プロパティが見つかりません。");
        var list = new List<TranscribedSegment>();
        try
        {
            while (await AwaitBooleanAsync(moveNextAsync.Invoke(enumerator, null)))
            {
                var current = currentProperty.GetValue(enumerator);
                if (current is not null) list.Add(CreateSegmentFromResultObject(current));
            }
        }
        finally { await DisposeUnknownAsync(enumerator); }
        return list;
    }

    private static IReadOnlyList<TranscribedSegment>? TryReadSegmentsFromEnumerable(object source)
    {
        if (source is not IEnumerable enumerable || source is string) return null;
        var list = new List<TranscribedSegment>();
        foreach (var item in enumerable) if (item is not null) list.Add(CreateSegmentFromResultObject(item));
        return list;
    }

    private static TranscribedSegment CreateSegmentFromResultObject(object source)
        => new(GetTimeSpan(source, "Start", "StartTime", "Begin", "Offset"), GetTimeSpan(source, "End", "EndTime", "Finish"), GetString(source, "Text", "Transcript", "Sentence"));

    private static TimeSpan GetTimeSpan(object target, params string[] names)
    {
        if (!TryGetPropertyValueByNames(target, names, out var value) || value is null) return TimeSpan.Zero;
        return value switch
        {
            TimeSpan ts => ts,
            double d => TimeSpan.FromSeconds(d),
            float f => TimeSpan.FromSeconds(f),
            long l => TimeSpan.FromMilliseconds(l),
            _ when TimeSpan.TryParse(value.ToString(), out var parsed) => parsed,
            _ => TimeSpan.Zero
        };
    }

    private static string GetString(object target, params string[] names)
        => TryGetPropertyValueByNames(target, names, out var value) ? value?.ToString()?.Trim() ?? string.Empty : string.Empty;

    private static bool TryGetPropertyValueByNames(object target, IReadOnlyList<string> names, out object? value)
    {
        foreach (var name in names)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property is null) continue;
            value = property.GetValue(target);
            if (value is string text)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                value = text;
                return true;
            }
            if (value is not null) return true;
        }
        value = null;
        return false;
    }

    private static async Task<object?> UnwrapAwaitableAsync(object value, CancellationToken cancellationToken)
    {
        if (value is Task task)
        {
            await task.WaitAsync(cancellationToken);
            return GetTaskResult(task);
        }
        if (TryConvertToTask(value, out var convertedTask))
        {
            await convertedTask.WaitAsync(cancellationToken);
            return GetTaskResult(convertedTask);
        }
        return value;
    }

    private static async Task<bool> AwaitBooleanAsync(object? awaitable)
    {
        if (awaitable is null) return false;
        if (awaitable is ValueTask<bool> valueTaskBool) return await valueTaskBool;
        if (awaitable is Task<bool> taskBool) return await taskBool;
        if (TryConvertToTask(awaitable, out var convertedTask))
        {
            if (convertedTask is Task<bool> convertedBoolTask) return await convertedBoolTask;
            await convertedTask;
            if (GetTaskResult(convertedTask) is bool result) return result;
        }
        throw new InvalidOperationException("MoveNextAsync の戻り値型に対応していません。");
    }

    private static bool TryConvertToTask(object source, out Task convertedTask)
    {
        convertedTask = null!;
        var asTask = source.GetType().GetMethod("AsTask", Type.EmptyTypes);
        if (asTask is null || !typeof(Task).IsAssignableFrom(asTask.ReturnType)) return false;
        if (asTask.Invoke(source, null) is not Task task) return false;
        convertedTask = task;
        return true;
    }

    private static object? GetTaskResult(Task task)
        => task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);

    private static async ValueTask DisposeUnknownAsync(object? instance)
    {
        switch (instance)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private static TranscriptionJobResult Fail(string message, DateTimeOffset started)
        => new(false, message, Array.Empty<string>(), started, DateTimeOffset.Now);
}
