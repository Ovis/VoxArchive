using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class WhisperTranscriptionService
{
    private static readonly object LogSync = new();
    private static readonly string LogFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoxArchive",
        "logs",
        "whisper-transcription.log");

    private readonly WhisperModelStore _modelStore;

    public WhisperTranscriptionService(WhisperModelStore modelStore)
    {
        _modelStore = modelStore;
    }

    public WhisperEnvironmentStatus CheckEnvironment(RecordingOptions options)
    {
        var runtimeAvailable = TryGetWhisperFactoryType(out _);
        var modelInstalled = _modelStore.IsInstalled(options.TranscriptionModel);

        var runtimeMessage = runtimeAvailable
            ? "Whisper.net ランタイムを検出しました。"
            : "Whisper.net ランタイムを検出できませんでした。";

        var modelMessage = modelInstalled
            ? $"モデル '{WhisperModelStore.GetModelFileName(options.TranscriptionModel)}' は配置済みです。"
            : $"モデル '{WhisperModelStore.GetModelFileName(options.TranscriptionModel)}' は未配置です。";

        var detail = runtimeAvailable && modelInstalled
            ? "文字起こし実行の前提条件を満たしています。"
            : "設定画面のモデル管理/依存関係を確認してください。";

        return new WhisperEnvironmentStatus(runtimeAvailable, modelInstalled, runtimeMessage, modelMessage, detail);
    }

    public async Task<string> EnsureModelAsync(TranscriptionModel model, CancellationToken cancellationToken = default)
    {
        if (_modelStore.IsInstalled(model))
        {
            return _modelStore.GetModelPath(model);
        }

        return await _modelStore.DownloadAsync(model, cancellationToken);
    }

    public async Task<TranscriptionJobResult> TranscribeAsync(TranscriptionJobRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        Log($"Transcribe start: file={request.AudioFilePath}, model={request.Options.TranscriptionModel}, lang={request.Options.TranscriptionLanguage}");

        if (!File.Exists(request.AudioFilePath))
        {
            Log("Transcribe failed: audio file missing");
            return Fail("対象ファイルが見つかりません。", started);
        }

        if (request.Options.TranscriptionOutputFormats == TranscriptionOutputFormats.None)
        {
            Log("Transcribe failed: output formats none");
            return Fail("出力形式が選択されていません。", started);
        }

        var modelPath = _modelStore.GetModelPath(request.Options.TranscriptionModel);
        if (!File.Exists(modelPath))
        {
            Log($"Transcribe failed: model file missing ({modelPath})");
            return Fail("モデルが未配置です。設定画面からモデルをダウンロードしてください。", started);
        }

        if (!TryGetWhisperFactoryType(out var factoryType))
        {
            Log("Transcribe failed: Whisper factory type not found");
            return Fail("Whisper.net ランタイムが利用できません。依存ライブラリを確認してください。", started);
        }

        try
        {
            var segments = await ExecuteWhisperAsync(factoryType!, modelPath, request, cancellationToken);
            var generated = await WriteOutputsAsync(request.AudioFilePath, request.Options.TranscriptionOutputFormats, segments, cancellationToken);

            Log($"Transcribe success: segments={segments.Count}, outputs={string.Join(",", generated.Select(Path.GetFileName))}");
            return new TranscriptionJobResult(
                Succeeded: true,
                Message: "文字起こしが完了しました。",
                GeneratedFiles: generated,
                StartedAt: started,
                FinishedAt: DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            Log("Transcribe exception", ex);
            return Fail($"文字起こしに失敗しました: {ex.Message} (詳細ログ: {LogFilePath})", started);
        }
    }

    public IReadOnlyList<string> BuildOutputPaths(string audioFilePath, TranscriptionOutputFormats formats)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(audioFilePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(audioFilePath));

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

    private static async Task<IReadOnlyList<TranscribedSegment>> ExecuteWhisperAsync(
        Type factoryType,
        string modelPath,
        TranscriptionJobRequest request,
        CancellationToken cancellationToken)
    {
        Log($"ExecuteWhisper: factoryType={factoryType.FullName}, modelPath={modelPath}");

        var fromPath = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FromPath" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        if (fromPath is null)
        {
            throw new InvalidOperationException("WhisperFactory.FromPath が見つかりません。");
        }

        object? factory = null;
        object? builder = null;
        object? processor = null;

        try
        {
            factory = fromPath.Invoke(null, [modelPath]);
            if (factory is null)
            {
                throw new InvalidOperationException("WhisperFactory の初期化に失敗しました。");
            }

            var createBuilder = factory.GetType().GetMethod("CreateBuilder", Type.EmptyTypes)
                ?? throw new InvalidOperationException("CreateBuilder が見つかりません。");

            builder = createBuilder.Invoke(factory, null)
                ?? throw new InvalidOperationException("Builder の生成に失敗しました。");

            var language = string.IsNullOrWhiteSpace(request.Options.TranscriptionLanguage)
                ? "auto"
                : request.Options.TranscriptionLanguage.Trim();
            if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var withLanguage = builder.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "WithLanguage" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (withLanguage is not null)
                {
                    builder = withLanguage.Invoke(builder, [language]) ?? builder;
                }
            }

            var build = builder.GetType().GetMethod("Build", Type.EmptyTypes)
                ?? throw new InvalidOperationException("Builder.Build が見つかりません。");
            processor = build.Invoke(builder, null)
                ?? throw new InvalidOperationException("Processor 生成に失敗しました。");

            await using var preparedInput = await PrepareWaveInputAsync(request.AudioFilePath, cancellationToken);

            var processAsync = processor.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ProcessAsync" && m.GetParameters().Length >= 1 && typeof(Stream).IsAssignableFrom(m.GetParameters()[0].ParameterType));

            if (processAsync is null)
            {
                throw new InvalidOperationException("Processor.ProcessAsync が見つかりません。");
            }

            var processArgs = BuildProcessArgs(processAsync, preparedInput.Stream, cancellationToken);
            var processResult = processAsync.Invoke(processor, processArgs)
                ?? throw new InvalidOperationException("ProcessAsync の戻り値が null です。");

            var resolvedResult = await UnwrapAwaitableAsync(processResult, cancellationToken)
                ?? throw new InvalidOperationException("ProcessAsync の解決結果が null です。");

            Log($"ProcessAsync result resolved type: {resolvedResult.GetType().FullName}");
            return await ReadSegmentsAsync(resolvedResult, cancellationToken);
        }
        finally
        {
            switch (processor)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            switch (factory)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
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

                list.Add(new TranscribedSegment(
                    GetTimeSpan(current, "Start", "StartTime", "Begin", "Offset"),
                    GetTimeSpan(current, "End", "EndTime", "Finish"),
                    GetString(current, "Text", "Transcript", "Sentence")));
            }
        }
        finally
        {
            switch (enumerator)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
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

            list.Add(new TranscribedSegment(
                GetTimeSpan(item, "Start", "StartTime", "Begin", "Offset"),
                GetTimeSpan(item, "End", "EndTime", "Finish"),
                GetString(item, "Text", "Transcript", "Sentence")));
        }

        return list;
    }

    private static async Task<object?> UnwrapAwaitableAsync(object value, CancellationToken cancellationToken)
    {
        if (value is Task task)
        {
            await task.WaitAsync(cancellationToken);
            return GetTaskResult(task);
        }

        var type = value.GetType();
        var asTask = type.GetMethod("AsTask", Type.EmptyTypes);
        if (asTask is not null && typeof(Task).IsAssignableFrom(asTask.ReturnType))
        {
            var taskValue = asTask.Invoke(value, null) as Task;
            if (taskValue is not null)
            {
                await taskValue.WaitAsync(cancellationToken);
                return GetTaskResult(taskValue);
            }
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

        var type = awaitable.GetType();
        var asTask = type.GetMethod("AsTask", Type.EmptyTypes);
        if (asTask is not null && typeof(Task).IsAssignableFrom(asTask.ReturnType))
        {
            var task = (Task?)asTask.Invoke(awaitable, null);
            if (task is Task<bool> booleanTask)
            {
                return await booleanTask;
            }

            if (task is not null)
            {
                await task;
                var resultProperty = task.GetType().GetProperty("Result");
                if (resultProperty?.GetValue(task) is bool boolResult)
                {
                    return boolResult;
                }
            }
        }

        throw new InvalidOperationException("MoveNextAsync の戻り値型に対応していません。");
    }

    private static async Task<PreparedWaveInput> PrepareWaveInputAsync(string audioFilePath, CancellationToken cancellationToken)
    {
        if (IsWaveFile(audioFilePath))
        {
            Log("PrepareWaveInput: input is already WAV");
            return new PreparedWaveInput(File.OpenRead(audioFilePath), null);
        }

        var tempWavePath = Path.Combine(Path.GetTempPath(), $"voxarchive-whisper-{Guid.NewGuid():N}.wav");
        Log($"PrepareWaveInput: convert to temp wav => {tempWavePath}");

        await Task.Run(() => ConvertAudioToWaveFile(audioFilePath, tempWavePath), cancellationToken);
        return new PreparedWaveInput(File.OpenRead(tempWavePath), tempWavePath);
    }

    private static void ConvertAudioToWaveFile(string sourcePath, string destinationPath)
    {
        using var reader = new AudioFileReader(sourcePath);
        ISampleProvider sampleProvider = reader;

        if (sampleProvider.WaveFormat.Channels == 2)
        {
            var mono = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
            sampleProvider = mono;
        }

        if (sampleProvider.WaveFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
        }

        WaveFileWriter.CreateWaveFile16(destinationPath, sampleProvider);
    }

    private static bool IsWaveFile(string filePath)
    {
        if (!string.Equals(Path.GetExtension(filePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) < 12)
            {
                return false;
            }

            return header[0] == (byte)'R'
                && header[1] == (byte)'I'
                && header[2] == (byte)'F'
                && header[3] == (byte)'F'
                && header[8] == (byte)'W'
                && header[9] == (byte)'A'
                && header[10] == (byte)'V'
                && header[11] == (byte)'E';
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan GetTimeSpan(object target, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is null)
            {
                continue;
            }

            var value = prop.GetValue(target);
            if (value is null)
            {
                continue;
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
        }

        return TimeSpan.Zero;
    }

    private static string GetString(object target, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var value = prop?.GetValue(target)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task<IReadOnlyList<string>> WriteOutputsAsync(
        string audioFilePath,
        TranscriptionOutputFormats formats,
        IReadOnlyList<TranscribedSegment> segments,
        CancellationToken cancellationToken)
    {
        var basePath = Path.Combine(Path.GetDirectoryName(audioFilePath) ?? string.Empty, Path.GetFileNameWithoutExtension(audioFilePath));
        var generated = new List<string>(4);

        if (formats.HasFlag(TranscriptionOutputFormats.Txt))
        {
            var path = basePath + ".txt";
            var text = string.Join(Environment.NewLine, segments.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
            await File.WriteAllTextAsync(path, text, System.Text.Encoding.UTF8, cancellationToken);
            generated.Add(path);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Srt))
        {
            var path = basePath + ".srt";
            var sb = new StringBuilder();
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                sb.AppendLine((i + 1).ToString());
                sb.AppendLine($"{FormatSrt(seg.Start)} --> {FormatSrt(seg.End)}");
                sb.AppendLine(seg.Text);
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(path, sb.ToString(), System.Text.Encoding.UTF8, cancellationToken);
            generated.Add(path);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Vtt))
        {
            var path = basePath + ".vtt";
            var sb = new StringBuilder();
            sb.AppendLine("WEBVTT");
            sb.AppendLine();
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                sb.AppendLine($"{FormatVtt(seg.Start)} --> {FormatVtt(seg.End)}");
                sb.AppendLine(seg.Text);
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(path, sb.ToString(), System.Text.Encoding.UTF8, cancellationToken);
            generated.Add(path);
        }

        if (formats.HasFlag(TranscriptionOutputFormats.Json))
        {
            var path = basePath + ".json";
            var payload = new
            {
                segments = segments.Select(x => new
                {
                    start = x.Start.TotalSeconds,
                    end = x.End.TotalSeconds,
                    text = x.Text
                })
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8, cancellationToken);
            generated.Add(path);
        }

        return generated;
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

    private static void Log(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ").AppendLine(message);
            if (ex is not null)
            {
                sb.AppendLine(ex.ToString());
            }

            lock (LogSync)
            {
                File.AppendAllText(LogFilePath, sb.ToString(), System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // ログ出力失敗で文字起こし本体を落とさない。
        }
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
                    // no-op
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record TranscribedSegment(TimeSpan Start, TimeSpan End, string Text);
}
