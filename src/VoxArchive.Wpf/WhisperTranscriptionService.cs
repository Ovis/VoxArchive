using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class WhisperTranscriptionService(WhisperModelStore modelStore)
{

    public WhisperEnvironmentStatus CheckEnvironment(RecordingOptions options)
    {
        try
        {
            var runtimeAvailable = TryGetWhisperFactoryType(out _);
            var modelInstalled = modelStore.IsInstalled(options.TranscriptionModel);

            // 設定画面の環境チェックではネイティブDLLのロードを避け、存在確認ベースで安全に判定する。
            var cudaRuntimeAvailable = TryProbeCudaRuntimeForSettings(out var cudaRuntimeDetail);
            var cudaDriverAvailable = TryProbeCudaDriverForSettings(out var cudaDriverDetail);
            var cudaAvailable = cudaRuntimeAvailable && cudaDriverAvailable;

            var runtimeMessage = runtimeAvailable
                ? "Whisper.net ランタイムを検出しました。"
                : "Whisper.net ランタイムを検出できませんでした。";

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
            return new WhisperEnvironmentStatus(
                RuntimeAvailable: false,
                ModelInstalled: false,
                RuntimeMessage: "環境チェック中に例外が発生しました。",
                ModelMessage: "モデル状態を判定できませんでした。",
                CudaAvailable: false,
                CudaMessage: "CUDA 判定中に例外が発生しました。",
                DetailMessage: ex.Message);
        }
    }

    private static bool TryProbeCudaRuntimeForSettings(out string detail)
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.X86 => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _ => "win-x64"
            };

            var candidates = new[]
            {
                Path.Combine(baseDir, "runtimes", "cuda", arch, "ggml-cuda-whisper.dll"),
                Path.Combine(baseDir, "runtimes", "cuda", "win-x64", "ggml-cuda-whisper.dll")
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
            var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "nvcuda.dll");
            if (File.Exists(system32))
            {
                detail = "nvcuda.dll を検出(System32)";
                return true;
            }

            var sysWow64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "nvcuda.dll");
            if (File.Exists(sysWow64))
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

    public async Task<TranscriptionJobResult> TranscribeAsync(TranscriptionJobRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        try
        {

            if (!File.Exists(request.AudioFilePath))
            {
                return Fail("対象ファイルが見つかりません。", started);
            }

            if (request.Options.TranscriptionOutputFormats == TranscriptionOutputFormats.None)
            {
                return Fail("出力形式が選択されていません。", started);
            }

            var modelPath = modelStore.GetModelPath(request.Options.TranscriptionModel);
            if (!File.Exists(modelPath))
            {
                return Fail("モデルが未配置です。設定画面からモデルをダウンロードしてください。", started);
            }

            if (!TryGetWhisperFactoryType(out var factoryType))
            {
                return Fail("Whisper.net ランタイムが利用できません。依存ライブラリを確認してください。", started);
            }
            var segments = await ExecuteWhisperAsync(factoryType!, modelPath, request, cancellationToken);
            var labeledSegments = await Task.Run(() => ApplySpeakerLabelsByChannelEnergy(request.AudioFilePath, segments, cancellationToken), cancellationToken);
            var generated = await WriteOutputsAsync(request.AudioFilePath, request.Options.TranscriptionModel, request.Options.TranscriptionOutputFormats, labeledSegments, cancellationToken);

            return new TranscriptionJobResult(
                Succeeded: true,
                Message: "文字起こしが完了しました。",
                GeneratedFiles: generated,
                StartedAt: started,
                FinishedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return Fail($"文字起こしに失敗しました: {ex.Message}", started);
        }
    }

    public static IReadOnlyList<string> BuildOutputPaths(string audioFilePath, TranscriptionModel model, TranscriptionOutputFormats formats)
    {
        var basePath = BuildOutputBasePath(audioFilePath, model);

        var list = new List<string>(4);
        if (formats.HasFlag(TranscriptionOutputFormats.Txt))
        {
            list.Add(basePath + ".txt");
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Srt))
        {
            list.Add(basePath + ".srt");
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Vtt))
        {
            list.Add(basePath + ".vtt");
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Json))
        {
            list.Add(basePath + ".json");
        }

        return list;
    }

    private static bool TryGetWhisperFactoryType(out Type? factoryType)
    {
        factoryType = null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            factoryType = assembly.GetTypes().FirstOrDefault(t => t.Name == "WhisperFactory");
            if (factoryType is not null)
            {
                return true;
            }
        }

        try
        {
            var loaded = Assembly.Load("Whisper.net");
            factoryType = loaded.GetTypes().FirstOrDefault(t => t.Name == "WhisperFactory");
            return factoryType is not null;
        }
        catch
        {
            return false;
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
        if (optionsType is null)
        {
            return null;
        }
        var options = Activator.CreateInstance(optionsType);
        if (options is null)
        {
            return null;
        }
        if (requestedUseGpu.HasValue)
        {
            var useGpuProperty = optionsType.GetProperty("UseGpu", BindingFlags.Public | BindingFlags.Instance);
            if (useGpuProperty?.CanWrite == true)
            {
                useGpuProperty.SetValue(options, requestedUseGpu.Value);
            }
        }
        return options;
    }
    private static async Task<IReadOnlyList<TranscribedSegment>> ExecuteWhisperAsync(
        Type factoryType,
        string modelPath,
        TranscriptionJobRequest request,
        CancellationToken cancellationToken)
    {

        var (fromPath, fromPathWithOptions) = FindFactoryFromPathMethods(factoryType);
        if (fromPath is null && fromPathWithOptions is null)
        {
            throw new InvalidOperationException("WhisperFactory.FromPath was not found.");
        }

        object? factory = null;
        object? builder = null;
        object? processor = null;

        try
        {
            var factoryOptions = CreateFactoryOptions(factoryType.Assembly, request.Options.TranscriptionExecutionMode);
            if (fromPathWithOptions is not null && factoryOptions is not null)
            {
                factory = fromPathWithOptions.Invoke(null, new object?[] { modelPath, factoryOptions });
            }
            else
            {
                factory = fromPath?.Invoke(null, new object?[] { modelPath });
            }

            if (factory is null)
            {
                throw new InvalidOperationException("WhisperFactory initialization failed.");
            }

            var createBuilder = factory.GetType().GetMethod("CreateBuilder", Type.EmptyTypes)
                ?? throw new InvalidOperationException("CreateBuilder was not found.");

            builder = createBuilder.Invoke(factory, null)
                ?? throw new InvalidOperationException("Builder creation failed.");

            var language = string.IsNullOrWhiteSpace(request.Options.TranscriptionLanguage)
                ? "auto"
                : request.Options.TranscriptionLanguage.Trim();
            if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var withLanguage = FindStringInstanceMethod(builder.GetType(), "WithLanguage");
                if (withLanguage is not null)
                {
                    builder = withLanguage.Invoke(builder, new object?[] { language }) ?? builder;
                }
            }

            var build = builder.GetType().GetMethod("Build", Type.EmptyTypes)
                ?? throw new InvalidOperationException("Builder.Build was not found.");
            processor = build.Invoke(builder, null)
                ?? throw new InvalidOperationException("Processor creation failed.");

            await using var preparedInput = await PrepareWaveInputAsync(request.AudioFilePath, request.Options, cancellationToken);
            var processAsync = FindProcessAsyncMethod(processor.GetType())
                ?? throw new InvalidOperationException("Processor.ProcessAsync was not found.");

            if (!string.IsNullOrWhiteSpace(preparedInput.TemporaryWavePath) && File.Exists(preparedInput.TemporaryWavePath))
            {
                var speechRegions = await DetectSpeechRegionsAsync(preparedInput.TemporaryWavePath!, cancellationToken);
                if (speechRegions.Count == 0)
                {
                    return Array.Empty<TranscribedSegment>();
                }

                var collected = new List<TranscribedSegment>();
                foreach (var region in speechRegions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segmentWavePath = CreateTemporarySegmentWavePath();
                    try
                    {
                        WriteMonoWaveSegment(preparedInput.TemporaryWavePath!, segmentWavePath, region);
                        await using var segmentStream = File.OpenRead(segmentWavePath);

                        var args = BuildProcessArgs(processAsync, segmentStream, cancellationToken);
                        var result = processAsync.Invoke(processor, args)
                            ?? throw new InvalidOperationException("ProcessAsync returned null.");

                        var resolved = await UnwrapAwaitableAsync(result, cancellationToken)
                            ?? throw new InvalidOperationException("ProcessAsync resolved to null.");

                        var segments = await ReadSegmentsAsync(resolved, cancellationToken);
                        if (segments.Count == 0)
                        {
                            continue;
                        }

                        collected.AddRange(OffsetSegments(segments, region.Start));
                    }
                    finally
                    {
                        TryDeleteFile(segmentWavePath);
                    }
                }

                return MergeAdjacentSegments(collected);
            }

            var fallbackArgs = BuildProcessArgs(processAsync, preparedInput.Stream, cancellationToken);
            var fallbackResult = processAsync.Invoke(processor, fallbackArgs)
                ?? throw new InvalidOperationException("ProcessAsync returned null.");
            var fallbackResolved = await UnwrapAwaitableAsync(fallbackResult, cancellationToken)
                ?? throw new InvalidOperationException("ProcessAsync resolved to null.");

            return await ReadSegmentsAsync(fallbackResolved, cancellationToken);
        }
        finally
        {
            await DisposeUnknownAsync(processor);
            await DisposeUnknownAsync(factory);
        }
    }


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

    private static async Task<IReadOnlyList<SpeechRegion>> DetectSpeechRegionsAsync(string monoWavePath, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            using var reader = new AudioFileReader(monoWavePath);
            if (reader.WaveFormat.Channels != 1)
            {
                return (IReadOnlyList<SpeechRegion>)new[]
                {
                    new SpeechRegion(TimeSpan.Zero, reader.TotalTime)
                };
            }

            var sampleRate = reader.WaveFormat.SampleRate;
            var frameSamples = Math.Max(1, (int)Math.Round(sampleRate * (VadFrameMilliseconds / 1000d)));
            var minSpeechFrames = Math.Max(1, (int)Math.Ceiling(VadMinSpeechMilliseconds / VadFrameMilliseconds));
            var minSilenceFrames = Math.Max(1, (int)Math.Ceiling(VadMinSilenceMilliseconds / VadFrameMilliseconds));
            var paddingFrames = Math.Max(0, (int)Math.Ceiling(VadSpeechPaddingMilliseconds / VadFrameMilliseconds));
            var mergeGapFrames = Math.Max(0, (int)Math.Ceiling(VadMergeGapMilliseconds / VadFrameMilliseconds));

            var dbFrames = new List<double>(VadAnalysisFrameCapacity);
            var buffer = new float[frameSamples];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = reader.Read(buffer, 0, frameSamples);
                if (read <= 0)
                {
                    break;
                }

                var sum = 0d;
                for (var i = 0; i < read; i++)
                {
                    var sample = buffer[i];
                    sum += sample * sample;
                }

                var rms = Math.Sqrt(sum / Math.Max(1, read));
                dbFrames.Add(ToDecibel(rms));
            }

            if (dbFrames.Count == 0)
            {
                return Array.Empty<SpeechRegion>();
            }

            var noiseFloorDb = Percentile(dbFrames, VadNoiseFloorPercentile);
            var speechThresholdDb = Math.Max(VadMinimumThresholdDb, noiseFloorDb + VadThresholdOffsetDb);

            var ranges = new List<(int StartFrame, int EndFrame)>();
            var inSpeech = false;
            var speechStart = 0;
            var trailingSilence = 0;

            for (var i = 0; i < dbFrames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isSpeechFrame = dbFrames[i] >= speechThresholdDb;
                if (!inSpeech)
                {
                    if (isSpeechFrame)
                    {
                        inSpeech = true;
                        speechStart = i;
                        trailingSilence = 0;
                    }

                    continue;
                }

                if (isSpeechFrame)
                {
                    trailingSilence = 0;
                    continue;
                }

                trailingSilence++;
                if (trailingSilence < minSilenceFrames)
                {
                    continue;
                }

                var speechEnd = i - trailingSilence;
                AddSpeechRangeIfValid(ranges, speechStart, speechEnd, minSpeechFrames);

                inSpeech = false;
                trailingSilence = 0;
            }

            if (inSpeech)
            {
                var speechEnd = dbFrames.Count - 1;
                AddSpeechRangeIfValid(ranges, speechStart, speechEnd, minSpeechFrames);
            }

            if (ranges.Count == 0)
            {
                return Array.Empty<SpeechRegion>();
            }

            for (var i = 0; i < ranges.Count; i++)
            {
                var start = Math.Max(0, ranges[i].StartFrame - paddingFrames);
                var end = Math.Min(dbFrames.Count - 1, ranges[i].EndFrame + paddingFrames);
                ranges[i] = (start, end);
            }

            var merged = new List<(int StartFrame, int EndFrame)> { ranges[0] };
            for (var i = 1; i < ranges.Count; i++)
            {
                var current = ranges[i];
                var last = merged[^1];
                if (current.StartFrame - last.EndFrame <= mergeGapFrames)
                {
                    merged[^1] = (last.StartFrame, Math.Max(last.EndFrame, current.EndFrame));
                    continue;
                }

                merged.Add(current);
            }

            var result = new List<SpeechRegion>(merged.Count);
            foreach (var range in merged)
            {
                var start = TimeSpan.FromSeconds((range.StartFrame * VadFrameMilliseconds) / 1000d);
                var end = TimeSpan.FromSeconds(((range.EndFrame + 1) * VadFrameMilliseconds) / 1000d);
                if (end > reader.TotalTime)
                {
                    end = reader.TotalTime;
                }

                if (end > start)
                {
                    result.Add(new SpeechRegion(start, end));
                }
            }

            return result;
        }, cancellationToken);
    }

    private static void WriteMonoWaveSegment(string sourceWavePath, string segmentWavePath, SpeechRegion region)
    {
        using var reader = new AudioFileReader(sourceWavePath);
        var segmentProvider = new OffsetSampleProvider(reader)
        {
            SkipOver = region.Start,
            Take = region.Duration
        };

        WaveFileWriter.CreateWaveFile16(segmentWavePath, segmentProvider);
    }

    private static IReadOnlyList<TranscribedSegment> OffsetSegments(IReadOnlyList<TranscribedSegment> segments, TimeSpan offset)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        var shifted = new List<TranscribedSegment>(segments.Count);
        foreach (var segment in segments)
        {
            shifted.Add(segment with
            {
                Start = segment.Start + offset,
                End = segment.End + offset
            });
        }

        return shifted;
    }

    private static IReadOnlyList<TranscribedSegment> MergeAdjacentSegments(IReadOnlyList<TranscribedSegment> segments)
    {
        if (segments.Count <= 1)
        {
            return segments;
        }

        var ordered = segments.OrderBy(x => x.Start).ToList();
        var merged = new List<TranscribedSegment>(ordered.Count) { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];

            if ((current.Start - last.End).TotalMilliseconds <= VadMergeGapMilliseconds
                && string.Equals(last.SpeakerLabel, current.SpeakerLabel, StringComparison.OrdinalIgnoreCase))
            {
                var text = MergeSegmentText(last.Text, current.Text);
                merged[^1] = last with
                {
                    End = current.End > last.End ? current.End : last.End,
                    Text = text
                };
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static void AddSpeechRangeIfValid(ICollection<(int StartFrame, int EndFrame)> ranges, int speechStart, int speechEnd, int minSpeechFrames)
    {
        if (speechEnd - speechStart + 1 >= minSpeechFrames)
        {
            ranges.Add((speechStart, speechEnd));
        }
    }

    private static string MergeSegmentText(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        return $"{left} {right}";
    }

    private static string CreateTemporarySegmentWavePath()
    {
        return Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-seg-{Guid.NewGuid():N}.wav");
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // 一時ファイル削除失敗はリトライ時に上書きされるため継続する。
        }
    }

    private static double ToDecibel(double rms)
    {
        return 20d * Math.Log10(Math.Max(rms, 1e-9d));
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return -120d;
        }

        var ordered = values.OrderBy(x => x).ToArray();
        var clamped = Math.Clamp(percentile, 0d, 1d);
        var index = (int)Math.Floor((ordered.Length - 1) * clamped);
        return ordered[index];
    }

    private static (MethodInfo? FromPath, MethodInfo? FromPathWithOptions) FindFactoryFromPathMethods(Type factoryType)
    {
        MethodInfo? fromPath = null;
        MethodInfo? fromPathWithOptions = null;

        foreach (var method in factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!string.Equals(method.Name, "FromPath", StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                fromPath = method;
                continue;
            }

            if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(string)
                && string.Equals(parameters[1].ParameterType.Name, "WhisperFactoryOptions", StringComparison.Ordinal))
            {
                fromPathWithOptions = method;
            }
        }

        return (fromPath, fromPathWithOptions);
    }

    private static MethodInfo? FindStringInstanceMethod(Type type, string methodName)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType == typeof(string));
    }

    private static MethodInfo? FindProcessAsyncMethod(Type processorType)
    {
        return processorType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "ProcessAsync"
                                 && m.GetParameters().Length >= 1
                                 && typeof(Stream).IsAssignableFrom(m.GetParameters()[0].ParameterType));
    }
    private static object?[] BuildProcessArgs(MethodInfo processAsync, Stream stream, CancellationToken cancellationToken)
    {
        var parameters = processAsync.GetParameters();
        if (parameters.Length == 1)
        {
            return [stream];
        }

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
        if (await TryReadSegmentsFromAsyncEnumerableAsync(source, cancellationToken) is { } asyncSegments)
        {
            return asyncSegments;
        }

        if (TryReadSegmentsFromEnumerable(source) is { } syncSegments)
        {
            return syncSegments;
        }

        throw new InvalidOperationException($"文字起こし結果の列挙型に対応していません: {source.GetType().FullName}");
    }

    private static async Task<IReadOnlyList<TranscribedSegment>?> TryReadSegmentsFromAsyncEnumerableAsync(object source, CancellationToken cancellationToken)
    {
        var asyncEnumerableInterface = source.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));
        if (asyncEnumerableInterface is null)
        {
            return null;
        }

        var getAsyncEnumerator = asyncEnumerableInterface.GetMethod("GetAsyncEnumerator");
        if (getAsyncEnumerator is null)
        {
            return null;
        }

        var enumArgs = getAsyncEnumerator.GetParameters().Length == 1
            ? [cancellationToken]
            : Array.Empty<object?>();

        var enumerator = getAsyncEnumerator.Invoke(source, enumArgs);
        if (enumerator is null)
        {
            throw new InvalidOperationException("IAsyncEnumerable の列挙取得に失敗しました。");
        }

        var asyncEnumeratorInterface = enumerator.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>));
        if (asyncEnumeratorInterface is null)
        {
            throw new InvalidOperationException("IAsyncEnumerator インターフェイス取得に失敗しました。");
        }

        var moveNextAsync = asyncEnumeratorInterface.GetMethod("MoveNextAsync")
            ?? throw new InvalidOperationException("MoveNextAsync が見つかりません。");
        var currentProperty = asyncEnumeratorInterface.GetProperty("Current")
            ?? throw new InvalidOperationException("Current プロパティが見つかりません。");

        var list = new List<TranscribedSegment>();
        try
        {
            while (await AwaitBooleanAsync(moveNextAsync.Invoke(enumerator, null)))
            {
                var current = currentProperty.GetValue(enumerator);
                if (current is null)
                {
                    continue;
                }

                list.Add(CreateSegmentFromResultObject(current));
            }
        }
        finally
        {
            await DisposeUnknownAsync(enumerator);
        }

        return list;
    }

    private static IReadOnlyList<TranscribedSegment>? TryReadSegmentsFromEnumerable(object source)
    {
        if (source is not IEnumerable enumerable || source is string)
        {
            return null;
        }

        var list = new List<TranscribedSegment>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            list.Add(CreateSegmentFromResultObject(item));
        }

        return list;
    }

    private static TranscribedSegment CreateSegmentFromResultObject(object source)
    {
        return new TranscribedSegment(
            GetTimeSpan(source, "Start", "StartTime", "Begin", "Offset"),
            GetTimeSpan(source, "End", "EndTime", "Finish"),
            GetString(source, "Text", "Transcript", "Sentence"));
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

    private static object? GetTaskResult(Task task)
    {
        var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return resultProperty?.GetValue(task);
    }

    private static async Task<bool> AwaitBooleanAsync(object? awaitable)
    {
        if (awaitable is null)
        {
            return false;
        }

        if (awaitable is ValueTask<bool> valueTaskBool)
        {
            return await valueTaskBool;
        }

        if (awaitable is Task<bool> taskBool)
        {
            return await taskBool;
        }

        if (TryConvertToTask(awaitable, out var convertedTask))
        {
            if (convertedTask is Task<bool> convertedBoolTask)
            {
                return await convertedBoolTask;
            }

            await convertedTask;
            if (GetTaskResult(convertedTask) is bool boolResult)
            {
                return boolResult;
            }
        }

        throw new InvalidOperationException("MoveNextAsync の戻り値型に対応していません。");
    }

    private static bool TryConvertToTask(object source, out Task convertedTask)
    {
        convertedTask = null!;

        var asTask = source.GetType().GetMethod("AsTask", Type.EmptyTypes);
        if (asTask is null || !typeof(Task).IsAssignableFrom(asTask.ReturnType))
        {
            return false;
        }

        var task = asTask.Invoke(source, null) as Task;
        if (task is null)
        {
            return false;
        }

        convertedTask = task;
        return true;
    }

    private const float TranscriptionSafePeak = 0.98f;
    private const double VadFrameMilliseconds = 30d;
    private const double VadMinSpeechMilliseconds = 250d;
    private const double VadMinSilenceMilliseconds = 600d;
    private const double VadSpeechPaddingMilliseconds = 200d;
    private const double VadMergeGapMilliseconds = 300d;
    private const int VadAnalysisFrameCapacity = 4096;
    private const double VadNoiseFloorPercentile = 0.2d;
    private const double VadMinimumThresholdDb = -50d;
    private const double VadThresholdOffsetDb = 12d;

    private static async Task<PreparedWaveInput> PrepareWaveInputAsync(string audioFilePath, RecordingOptions options, CancellationToken cancellationToken)
    {
        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-{Guid.NewGuid():N}.wav");

        await Task.Run(() => ConvertAudioToWaveFile(audioFilePath, tempWavePath, options), cancellationToken);
        return new PreparedWaveInput(File.OpenRead(tempWavePath), tempWavePath);
    }

    private static void ConvertAudioToWaveFile(string sourcePath, string destinationPath, RecordingOptions options)
    {
        var tempRawWavePath = destinationPath + ".raw.tmp";
        try
        {
            using var reader = new AudioFileReader(sourcePath);
            var sampleProvider = BuildTranscriptionSampleProvider(reader, options);
            var firstPassPeak = WriteSampleProviderAsPcm16Wave(sampleProvider, tempRawWavePath, 1f);

            if (firstPassPeak <= TranscriptionSafePeak)
            {
                File.Move(tempRawWavePath, destinationPath, true);
                return;
            }

            var safeScale = (float)Math.Clamp(TranscriptionSafePeak / firstPassPeak, 0f, 1f);
            using var normalizationReader = new AudioFileReader(tempRawWavePath);
            WriteSampleProviderAsPcm16Wave(normalizationReader, destinationPath, safeScale);
            File.Delete(tempRawWavePath);
        }
        finally
        {
            if (File.Exists(tempRawWavePath))
            {
                File.Delete(tempRawWavePath);
            }
        }
    }

    private static ISampleProvider BuildTranscriptionSampleProvider(ISampleProvider source, RecordingOptions options)
    {
        var provider = source;

        var speakerGain = (float)Math.Clamp(DbToLinearGain(options.DefaultSpeakerPlaybackGainDb), 0.01d, 8d);
        var micGain = (float)Math.Clamp(DbToLinearGain(options.DefaultMicPlaybackGainDb), 0.01d, 8d);

        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f * speakerGain,
                RightVolume = 0.5f * micGain
            };
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            var firstTwoChannels = new MultiplexingSampleProvider(new[] { provider }, 2);
            firstTwoChannels.ConnectInputToOutput(0, 0);
            firstTwoChannels.ConnectInputToOutput(1, 1);
            provider = new StereoToMonoSampleProvider(firstTwoChannels)
            {
                LeftVolume = 0.5f * speakerGain,
                RightVolume = 0.5f * micGain
            };
        }
        else
        {
            var monoGain = (float)Math.Clamp(DbToLinearGain((options.DefaultSpeakerPlaybackGainDb + options.DefaultMicPlaybackGainDb) / 2d), 0.01d, 8d);
            if (Math.Abs(monoGain - 1f) > 0.0001f)
            {
                provider = new VolumeSampleProvider(provider) { Volume = monoGain };
            }
        }

        if (provider.WaveFormat.SampleRate != 16000)
        {
            provider = new WdlResamplingSampleProvider(provider, 16000);
        }

        if (provider.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException($"Whisper input must be mono. Actual channels: {provider.WaveFormat.Channels}");
        }

        return provider;
    }

    private static float WriteSampleProviderAsPcm16Wave(ISampleProvider provider, string destinationPath, float outputScale)
    {
        if (provider.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException($"PCM16 writer expects mono input. Actual channels: {provider.WaveFormat.Channels}");
        }

        var peak = 0f;
        var sampleBuffer = new float[Math.Max(4096, provider.WaveFormat.SampleRate / 2)];
        var pcmBuffer = new byte[sampleBuffer.Length * 2];

        using var writer = new WaveFileWriter(destinationPath, new WaveFormat(provider.WaveFormat.SampleRate, 16, 1));

        while (true)
        {
            var read = provider.Read(sampleBuffer, 0, sampleBuffer.Length);
            if (read <= 0)
            {
                break;
            }

            var offset = 0;
            for (var i = 0; i < read; i++)
            {
                var scaled = sampleBuffer[i] * outputScale;
                var abs = Math.Abs(scaled);
                if (abs > peak)
                {
                    peak = abs;
                }

                var clamped = Math.Clamp(scaled, -1f, 1f);
                var pcm = (short)Math.Round(clamped * short.MaxValue);
                pcmBuffer[offset++] = (byte)(pcm & 0xFF);
                pcmBuffer[offset++] = (byte)((pcm >> 8) & 0xFF);
            }

            writer.Write(pcmBuffer, 0, read * 2);
        }

        return peak;
    }

    private static double DbToLinearGain(double gainDb)
    {
        return Math.Pow(10d, gainDb / 20d);
    }

    private static TimeSpan GetTimeSpan(object target, params string[] names)
    {
        if (!TryGetPropertyValueByNames(target, names, out var value) || value is null)
        {
            return TimeSpan.Zero;
        }

        if (value is TimeSpan ts)
        {
            return ts;
        }

        if (value is double d)
        {
            return TimeSpan.FromSeconds(d);
        }

        if (value is float f)
        {
            return TimeSpan.FromSeconds(f);
        }

        if (value is long l)
        {
            return TimeSpan.FromMilliseconds(l);
        }

        if (TimeSpan.TryParse(value.ToString(), out var parsed))
        {
            return parsed;
        }

        return TimeSpan.Zero;
    }

    private static string GetString(object target, params string[] names)
    {
        return TryGetPropertyValueByNames(target, names, out var value)
            ? value?.ToString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool TryGetPropertyValueByNames(object target, IReadOnlyList<string> names, out object? value)
    {
        foreach (var name in names)
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null)
            {
                continue;
            }

            value = prop.GetValue(target);
            if (value is string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                value = text;
                return true;
            }

            if (value is not null)
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static async Task<IReadOnlyList<string>> WriteOutputsAsync(
        string audioFilePath,
        TranscriptionModel model,
        TranscriptionOutputFormats formats,
        IReadOnlyList<TranscribedSegment> segments,
        CancellationToken cancellationToken)
    {
        var basePath = BuildOutputBasePath(audioFilePath, model);
        var generated = new List<string>(4);

        if (formats.HasFlag(TranscriptionOutputFormats.Txt))
        {
            var path = basePath + ".txt";
            var text = string.Join(Environment.NewLine, segments.Select(FormatSegmentText).Where(x => !string.IsNullOrWhiteSpace(x)));
            await WriteOutputFileAsync(path, text, generated, cancellationToken);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Srt))
        {
            var path = basePath + ".srt";
            var text = BuildSrtContent(segments);
            await WriteOutputFileAsync(path, text, generated, cancellationToken);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Vtt))
        {
            var path = basePath + ".vtt";
            var text = BuildVttContent(segments);
            await WriteOutputFileAsync(path, text, generated, cancellationToken);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Json))
        {
            var path = basePath + ".json";
            var text = BuildJsonContent(segments);
            await WriteOutputFileAsync(path, text, generated, cancellationToken);
        }

        return generated;
    }

    private static async Task WriteOutputFileAsync(string path, string text, ICollection<string> generated, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8, cancellationToken);
        generated.Add(path);
    }

    private static string BuildSrtContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{FormatSrt(seg.Start)} --> {FormatSrt(seg.End)}");
            sb.AppendLine(FormatSegmentText(seg));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildVttContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        foreach (var seg in segments)
        {
            sb.AppendLine($"{FormatVtt(seg.Start)} --> {FormatVtt(seg.End)}");
            sb.AppendLine(FormatSegmentText(seg));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildJsonContent(IReadOnlyList<TranscribedSegment> segments)
    {
        var payload = new
        {
            segments = segments.Select(x => new
            {
                start = x.Start.TotalSeconds,
                end = x.End.TotalSeconds,
                speaker = x.SpeakerLabel,
                text = x.Text
            })
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }



    private static IReadOnlyList<TranscribedSegment> ApplySpeakerLabelsByChannelEnergy(
        string audioFilePath,
        IReadOnlyList<TranscribedSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        try
        {
            using var reader = new AudioFileReader(audioFilePath);
            if (reader.WaveFormat.Channels < 2)
            {
                return segments;
            }

            var sampleRate = reader.WaveFormat.SampleRate;
            var channels = reader.WaveFormat.Channels;
            var ranges = BuildSegmentFrameRanges(segments, sampleRate);
            var leftEnergy = new double[segments.Count];
            var rightEnergy = new double[segments.Count];
            var buffer = new float[Math.Max(4096, sampleRate / 4) * channels];

            var segmentIndex = 0;
            long frameIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var frames = read / channels;
                for (var frame = 0; frame < frames; frame++, frameIndex++)
                {
                    while (segmentIndex < ranges.Count && frameIndex >= ranges[segmentIndex].EndFrame)
                    {
                        segmentIndex++;
                    }

                    if (segmentIndex >= ranges.Count)
                    {
                        break;
                    }

                    var range = ranges[segmentIndex];
                    if (frameIndex < range.StartFrame)
                    {
                        continue;
                    }

                    var sampleIndex = frame * channels;
                    var left = buffer[sampleIndex];
                    var right = buffer[sampleIndex + 1];
                    leftEnergy[segmentIndex] += left * left;
                    rightEnergy[segmentIndex] += right * right;
                }

                if (segmentIndex >= ranges.Count)
                {
                    break;
                }
            }

            var labeled = new List<TranscribedSegment>(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                var label = ResolveSpeakerLabel(leftEnergy[i], rightEnergy[i]);
                labeled.Add(segments[i] with { SpeakerLabel = label });
            }

            return labeled;
        }
        catch
        {
            // 話者ラベル付与に失敗しても文字起こし自体は継続する。
            return segments;
        }
    }

    private static IReadOnlyList<SegmentFrameRange> BuildSegmentFrameRanges(IReadOnlyList<TranscribedSegment> segments, int sampleRate)
    {
        var ranges = new List<SegmentFrameRange>(segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            var startSeconds = Math.Max(0d, segments[i].Start.TotalSeconds);
            var endSeconds = Math.Max(startSeconds + 0.02d, segments[i].End.TotalSeconds);
            var startFrame = (long)Math.Floor(startSeconds * sampleRate);
            var endFrame = (long)Math.Ceiling(endSeconds * sampleRate);
            if (endFrame <= startFrame)
            {
                endFrame = startFrame + 1;
            }

            ranges.Add(new SegmentFrameRange(startFrame, endFrame));
        }

        return ranges;
    }

    private static string ResolveSpeakerLabel(double leftEnergy, double rightEnergy)
    {
        const double epsilon = 1e-10;
        const double sameLevelThresholdDb = 2.5;

        var diffDb = 10d * Math.Log10((rightEnergy + epsilon) / (leftEnergy + epsilon));
        if (Math.Abs(diffDb) < sameLevelThresholdDb)
        {
            return "Mixed";
        }

        return diffDb > 0 ? "Mic" : "Speaker";
    }

    private static string FormatSegmentText(TranscribedSegment segment)
    {
        var text = segment.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(segment.SpeakerLabel))
        {
            return text;
        }

        return $"[{segment.SpeakerLabel}] {text}";
    }
    private static string BuildOutputBasePath(string audioFilePath, TranscriptionModel model)
    {
        var directory = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(audioFilePath);
        var modelName = GetModelDisplayName(model);
        return Path.Combine(directory, $"{fileName}-{modelName}");
    }

    private static string GetModelDisplayName(TranscriptionModel model)
    {
        return model switch
        {
            TranscriptionModel.Tiny => "tiny",
            TranscriptionModel.Base => "base",
            TranscriptionModel.Small => "small",
            TranscriptionModel.Medium => "medium",
            TranscriptionModel.LargeV3 => "large-v3",
            _ => model.ToString().ToLowerInvariant()
        };
    }
    private static string FormatSrt(TimeSpan time)
    {
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static string FormatVtt(TimeSpan time)
    {
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static TranscriptionJobResult Fail(string message, DateTimeOffset started)
    {
        return new TranscriptionJobResult(
            Succeeded: false,
            Message: message,
            GeneratedFiles: Array.Empty<string>(),
            StartedAt: started,
            FinishedAt: DateTimeOffset.Now);
    }


    private sealed class PreparedWaveInput : IAsyncDisposable
    {
        public PreparedWaveInput(Stream stream, string? temporaryWavePath)
        {
            Stream = stream;
            TemporaryWavePath = temporaryWavePath;
        }

        public Stream Stream { get; }
        public string? TemporaryWavePath { get; }

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            if (!string.IsNullOrWhiteSpace(TemporaryWavePath) && File.Exists(TemporaryWavePath))
            {
                try
                {
                    File.Delete(TemporaryWavePath);
                }
                catch
                {
                    // 一時ファイル削除失敗は後続実行で上書き可能なため継続する。
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record SpeechRegion(TimeSpan Start, TimeSpan End)
    {
        public TimeSpan Duration => End - Start;
    }

    private sealed record SegmentFrameRange(long StartFrame, long EndFrame);
    private sealed record TranscribedSegment(TimeSpan Start, TimeSpan End, string Text, string? SpeakerLabel = null);
}
