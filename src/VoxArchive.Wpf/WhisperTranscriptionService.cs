using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxArchive.Domain;

namespace VoxArchive.Wpf;

public sealed class WhisperTranscriptionService
{

    private static readonly object CudaNativeLoadSync = new();
    private static readonly List<IntPtr> CudaNativeHandles = new();

    private readonly WhisperModelStore _modelStore;

    public WhisperTranscriptionService(WhisperModelStore modelStore)
    {
        _modelStore = modelStore;
    }

    public WhisperEnvironmentStatus CheckEnvironment(RecordingOptions options)
    {
        try
        {
            var runtimeAvailable = TryGetWhisperFactoryType(out _);
            var modelInstalled = _modelStore.IsInstalled(options.TranscriptionModel);

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

            var modelPath = _modelStore.GetModelPath(request.Options.TranscriptionModel);
            if (!File.Exists(modelPath))
            {
                return Fail("モデルが未配置です。設定画面からモデルをダウンロードしてください。", started);
            }

            if (!TryGetWhisperFactoryType(out var factoryType))
            {
                return Fail("Whisper.net ランタイムが利用できません。依存ライブラリを確認してください。", started);
            }

            if (request.Options.TranscriptionExecutionMode == TranscriptionExecutionMode.CudaPreferred)
            {

                // 事前ロードは行わず、存在確認ベースの判定に限定してクラッシュ経路を避ける。
                var cudaRuntimeAvailable = TryProbeCudaRuntimeForSettings(out var cudaRuntimeDetail);
                var cudaDriverAvailable = TryProbeCudaDriverForSettings(out var cudaDriverDetail);
                var cudaReady = cudaRuntimeAvailable && cudaDriverAvailable;
                if (!cudaReady)
                {
                }
            }

            var segments = await ExecuteWhisperAsync(factoryType!, modelPath, request, cancellationToken);
            var generated = await WriteOutputsAsync(request.AudioFilePath, request.Options.TranscriptionModel, request.Options.TranscriptionOutputFormats, segments, cancellationToken);

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


    private static bool TryGetCudaRuntimeType(out string detail)
    {
        var pathAdjustDetail = EnsureCudaToolkitBinOnPath();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(assemblyName)
                || !assemblyName.StartsWith("Whisper.net.Runtime.Cuda", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            detail = $"assembly loaded: {assemblyName}; {pathAdjustDetail}";
            return true;
        }

        try
        {
            var loaded = Assembly.Load("Whisper.net.Runtime.Cuda");
            detail = $"assembly loaded: {loaded.GetName().Name}; {pathAdjustDetail}";
            return true;
        }
        catch
        {
            // Whisper.net.Runtime.Cuda may provide native assets only.
        }

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
            if (NativeLibrary.TryLoad(found, out var nativeHandle))
            {
                NativeLibrary.Free(nativeHandle);
                detail = $"native runtime loadable: {Path.GetFileName(found)}; {pathAdjustDetail}";
                return true;
            }

            detail = BuildNativeLoadFailureDetail(found) + $"; {pathAdjustDetail}";
            return false;
        }

        detail = $"cuda runtime assets not found; {pathAdjustDetail}";
        return false;
    }

    private static bool TryLoadCudaDriver(out string detail)
    {
        IntPtr handle;
        if (NativeLibrary.TryLoad("nvcuda.dll", out handle))
        {
            NativeLibrary.Free(handle);
            detail = "nvcuda.dll を検出";
            return true;
        }

        detail = "nvcuda.dll を検出できません";
        return false;
    }

    private static bool TryPreloadCudaRuntimeBinaries(out string detail)
    {
        var pathAdjustDetail = EnsureCudaToolkitBinOnPath();
        var runtimeDir = GetCudaRuntimeDirectory();
        if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
        {
            detail = $"cuda runtime directory not found; {pathAdjustDetail}";
            return false;
        }

        var loadOrder = new[]
        {
            "ggml-base-whisper.dll",
            "ggml-cpu-whisper.dll",
            "ggml-whisper.dll",
            "whisper.dll",
            "ggml-cuda-whisper.dll"
        };

        lock (CudaNativeLoadSync)
        {
            if (CudaNativeHandles.Count > 0)
            {
                detail = $"preload already initialized; {pathAdjustDetail}";
                return true;
            }

            foreach (var fileName in loadOrder)
            {
                var fullPath = Path.Combine(runtimeDir, fileName);
                if (!File.Exists(fullPath))
                {
                    detail = $"missing runtime file: {fileName}; {pathAdjustDetail}";
                    return false;
                }

                if (!NativeLibrary.TryLoad(fullPath, out var handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    var message = new Win32Exception(error).Message;
                    detail = $"failed to preload {fileName} (Win32Error={error}: {message}); {pathAdjustDetail}";
                    return false;
                }

                CudaNativeHandles.Add(handle);
            }
        }

        detail = $"preload ok; {pathAdjustDetail}";
        return true;
    }

    private static string? GetCudaRuntimeDirectory()
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
            Path.Combine(baseDir, "runtimes", "cuda", arch),
            Path.Combine(baseDir, "runtimes", "cuda", "win-x64")
        };

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static string EnsureCudaToolkitBinOnPath()
    {
        var paths = new List<string>();

        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cudaPath))
        {
            paths.Add(Path.Combine(cudaPath, "bin"));
        }

        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            if (item.Key is not string key || item.Value is not string value)
            {
                continue;
            }

            if (!key.StartsWith("CUDA_PATH_V", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            paths.Add(Path.Combine(value, "bin"));
        }

        var candidates = paths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return "no cuda toolkit path found";
        }

        lock (CudaNativeLoadSync)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var added = new List<string>();
            foreach (var candidate in candidates)
            {
                if (currentPath.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                currentPath = candidate + ";" + currentPath;
                added.Add(candidate);
            }

            if (added.Count > 0)
            {
                Environment.SetEnvironmentVariable("PATH", currentPath);
                return "added cuda bin: " + string.Join(" | ", added);
            }
        }

        return "cuda bin already on PATH";
    }

    private static string ProbeCudaDependencyLibraries()
    {
        EnsureCudaToolkitBinOnPath();

        var names = new[]
        {
            "cudart64_13.dll",
            "cublas64_13.dll",
            "cublasLt64_13.dll",
        };

        var result = new List<string>(names.Length);
        foreach (var name in names)
        {
            var handle = LoadLibraryW(name);
            if (handle != IntPtr.Zero)
            {
                FreeLibrary(handle);
                result.Add($"{name}=ok");
                continue;
            }

            var error = Marshal.GetLastWin32Error();
            var message = new Win32Exception(error).Message;
            result.Add($"{name}=ng({error}:{message})");
        }

        return string.Join(", ", result);
    }

    private static string BuildNativeLoadFailureDetail(string libraryPath)
    {
        var fileName = Path.GetFileName(libraryPath);
        IntPtr moduleHandle = LoadLibraryW(libraryPath);
        if (moduleHandle != IntPtr.Zero)
        {
            FreeLibrary(moduleHandle);
            return $"native runtime loadable by LoadLibraryW but failed with NativeLibrary.TryLoad: {fileName}";
        }

        var error = Marshal.GetLastWin32Error();
        var message = new Win32Exception(error).Message;
        if (error == 126)
        {
            var deps = ProbeCudaDependencyLibraries();
            return $"native runtime found but failed to load: {fileName} (Win32Error={error}: {message}); dependencyProbe=[{deps}]";
        }

        return $"native runtime found but failed to load: {fileName} (Win32Error={error}: {message})";
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    private static object? CreateFactoryOptions(Assembly whisperAssembly, TranscriptionExecutionMode mode, out bool? requestedUseGpu)
    {
        requestedUseGpu = mode switch
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

        var fromPath = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "FromPath" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        var fromPathWithOptions = factoryType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (m.Name != "FromPath")
                {
                    return false;
                }

                var parameters = m.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && string.Equals(parameters[1].ParameterType.Name, "WhisperFactoryOptions", StringComparison.Ordinal);
            });
        if (fromPath is null && fromPathWithOptions is null)
        {
            throw new InvalidOperationException("WhisperFactory.FromPath が見つかりません。");
        }

        object? factory = null;
        object? builder = null;
        object? processor = null;

        try
        {
            var factoryOptions = CreateFactoryOptions(factoryType.Assembly, request.Options.TranscriptionExecutionMode, out var requestedUseGpu);
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
            string? runtimeInfoText = null;
            var runtimeInfoMethod = factory.GetType().GetMethod("GetRuntimeInfo", Type.EmptyTypes);
            if (runtimeInfoMethod?.Invoke(factory, null) is string runtimeInfo && !string.IsNullOrWhiteSpace(runtimeInfo))
            {
                runtimeInfoText = runtimeInfo.Trim();
                if (request.Options.TranscriptionExecutionMode == TranscriptionExecutionMode.CudaPreferred
                    && !runtimeInfoText.Contains("CUDA", StringComparison.OrdinalIgnoreCase))
                {
                }
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
                    builder = withLanguage.Invoke(builder, new object?[] { language }) ?? builder;
                }
            }

            var build = builder.GetType().GetMethod("Build", Type.EmptyTypes)
                ?? throw new InvalidOperationException("Builder.Build が見つかりません。");
            processor = build.Invoke(builder, null)
                ?? throw new InvalidOperationException("Processor 生成に失敗しました。");

            await using var preparedInput = await PrepareWaveInputAsync(request.AudioFilePath, request.Options, cancellationToken);

            var processAsync = processor.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ProcessAsync" && m.GetParameters().Length >= 1 && typeof(Stream).IsAssignableFrom(m.GetParameters()[0].ParameterType));

            if (processAsync is null)
            {
                throw new InvalidOperationException("Processor.ProcessAsync が見つかりません。");
            }

            var processArgs = BuildProcessArgs(processAsync, preparedInput.Stream, cancellationToken);
            var processResult = processAsync.Invoke(processor, processArgs)
                ?? throw new InvalidOperationException("ProcessAsync の戻り値が null です。");
            var probeAfterInvoke = ProbeCudaUsage();


            var resolvedResult = await UnwrapAwaitableAsync(processResult, cancellationToken)
                ?? throw new InvalidOperationException("ProcessAsync の解決結果が null です。");

            var segments = await ReadSegmentsAsync(resolvedResult, cancellationToken);
            var probeAfterRead = ProbeCudaUsage();
            var runtimeCudaFlag = TryParseRuntimeBackendFlag(runtimeInfoText, "CUDA");
            var runtimeCpuFlag = TryParseRuntimeBackendFlag(runtimeInfoText, "CPU");
            var computeDetected = probeAfterInvoke.ComputeProcessDetected || probeAfterRead.ComputeProcessDetected;
            var moduleDetected = probeAfterInvoke.CudaModuleLoaded || probeAfterRead.CudaModuleLoaded;

            var confirmed = moduleDetected || (runtimeCudaFlag == true && computeDetected);
            var probable = !confirmed && (runtimeCudaFlag == true || moduleDetected);
            var conclusion = confirmed
                ? "confirmed"
                : probable
                    ? "probable"
                    : "not-detected";


            return segments;
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

    private static CudaUsageProbeResult ProbeCudaUsage()
    {
        var moduleLoaded = TryDetectLoadedCudaModule(out var moduleDetail);
        var computeDetected = TryDetectCudaComputeProcess(out var computeDetail);
        return new CudaUsageProbeResult(moduleLoaded, moduleDetail, computeDetected, computeDetail);
    }
    private static bool TryDetectLoadedCudaModule(out string detail)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var fileName = Path.GetFileName(module.ModuleName);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    continue;
                }
                if (fileName.Contains("ggml-cuda-whisper", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("cudart", StringComparison.OrdinalIgnoreCase)
                    || fileName.Contains("cublas", StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"loaded: {fileName}";
                    return true;
                }
            }
            detail = "cuda module not loaded";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"module detect failed: {ex.GetType().Name}";
            return false;
        }
    }
    private static bool TryDetectCudaComputeProcess(out string detail)
    {
        try
        {
            var currentPid = Process.GetCurrentProcess().Id.ToString();
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                detail = "nvidia-smi process start failed";
                return false;
            }
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1500))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // ignore
                }
                detail = "nvidia-smi timeout";
                return false;
            }
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(',');
                for (var i = 0; i < parts.Length; i++)
                {
                    parts[i] = parts[i].Trim();
                }
                if (parts.Length == 0)
                {
                    continue;
                }
                if (string.Equals(parts[0], currentPid, StringComparison.Ordinal))
                {
                    var memoryToken = parts.Length >= 3 ? parts[2].Trim().TrimStart('[').TrimEnd(']') : string.Empty;
                    if (int.TryParse(memoryToken, out var usedMemoryMb) && usedMemoryMb > 0)
                    {
                        detail = $"compute attached: {line.Trim()}";
                        return true;
                    }

                    if (string.Equals(memoryToken, "N/A", StringComparison.OrdinalIgnoreCase))
                    {
                        detail = $"pid matched but used_gpu_memory is N/A: {line.Trim()}";
                        return false;
                    }

                    detail = $"pid matched but used_gpu_memory is invalid ('{memoryToken}'): {line.Trim()}";
                    return false;
                }

            }
            detail = "compute process not listed";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"nvidia-smi unavailable: {ex.GetType().Name}";
            return false;
        }
    }

    private static bool? TryParseRuntimeBackendFlag(string? runtimeInfoText, string backendName)
    {
        if (string.IsNullOrWhiteSpace(runtimeInfoText))
        {
            return null;
        }

        var token = backendName + " = ";
        var idx = runtimeInfoText.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var valueIndex = idx + token.Length;
        if (valueIndex >= runtimeInfoText.Length)
        {
            return null;
        }

        var valueChar = runtimeInfoText[valueIndex];
        if (valueChar == '1')
        {
            return true;
        }

        if (valueChar == '0')
        {
            return false;
        }

        return null;
    }
    private static object?[] BuildProcessArgs(MethodInfo processAsync, Stream stream, CancellationToken cancellationToken)
    {
        var parameters = processAsync.GetParameters();
        if (parameters.Length == 1)
        {
            return new object?[] { stream };
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
            ? new object?[] { cancellationToken }
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

    private const float TranscriptionSafePeak = 0.98f;

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
        ISampleProvider provider = source;

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
            foreach (var seg in segments)
            {
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


    private sealed record CudaUsageProbeResult(
        bool CudaModuleLoaded,
        string ModuleDetail,
        bool ComputeProcessDetected,
        string ComputeDetail);
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



