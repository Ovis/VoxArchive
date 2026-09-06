using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoxArchive.Transcription.Whisper;

/// <summary>
/// WhisperFactoryとProcessorの生成、およびnative runtime選択を一か所で管理する
/// </summary>
public sealed class WhisperProcessorFactory
{
    private static readonly object RuntimeSelectionGate = new();

    /// <summary>
    /// Job snapshotに従ってWhisper processorを生成する
    /// </summary>
    /// <param name="options">Job Admissionでモデルパスまで解決済みのWhisper options</param>
    public WhisperProcessorSession Create(WhisperEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("Whisperモデルが配置されていません。", options.ModelPath);
        }

        lock (RuntimeSelectionGate)
        {
            ConfigureRuntime(options.ExecutionMode);

            var factoryOptions = WhisperFactoryOptions.Default;
            factoryOptions.UseGpu = options.ExecutionMode != WhisperExecutionMode.Cpu;
            var factory = WhisperFactory.FromPath(options.ModelPath, factoryOptions);
            try
            {
                var builder = factory.CreateBuilder();
                builder.WithLanguage(string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language.Trim());
                var processor = builder.Build();

                var loaded = RuntimeOptions.LoadedLibrary
                    ?? throw new InvalidOperationException("Whisper native runtimeのロード結果を取得できませんでした。");
                if (!IsCompatible(options.ExecutionMode, loaded))
                {
                    processor.Dispose();
                    throw new InvalidOperationException(
                        $"要求したWhisper backendとロードされたruntimeが一致しません。Requested={options.ExecutionMode}, Actual={loaded}");
                }

                return new WhisperProcessorSession(factory, processor, loaded.ToString());
            }
            catch
            {
                factory.Dispose();
                throw;
            }
        }
    }

    private static void ConfigureRuntime(WhisperExecutionMode mode)
    {
        if (RuntimeOptions.LoadedLibrary is { } loaded)
        {
            // Whisper.netのnative runtimeはプロセス内で一度ロードされると後続Factoryでも共有される。
            // 既にロード済みのruntimeを別backendへ見せかけて実行せず、明示要求と不一致なら失敗させる。
            if (!IsCompatible(mode, loaded))
            {
                throw new InvalidOperationException(
                    $"Whisper runtimeは既に別backendでロード済みです。Requested={mode}, Loaded={loaded}");
            }
            return;
        }

        RuntimeOptions.RuntimeLibraryOrder = mode switch
        {
            WhisperExecutionMode.Auto =>
                [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            WhisperExecutionMode.Cpu =>
                [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            WhisperExecutionMode.Cuda =>
                [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
            WhisperExecutionMode.Vulkan =>
                [RuntimeLibrary.Vulkan],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static bool IsCompatible(WhisperExecutionMode mode, RuntimeLibrary loaded)
    {
        return mode switch
        {
            WhisperExecutionMode.Auto => true,
            WhisperExecutionMode.Cpu => loaded is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx,
            WhisperExecutionMode.Cuda => loaded is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12,
            WhisperExecutionMode.Vulkan => loaded == RuntimeLibrary.Vulkan,
            _ => false
        };
    }
}

/// <summary>
/// WhisperFactoryとProcessorを同じライフサイクルで保持する
/// </summary>
public sealed class WhisperProcessorSession(
    WhisperFactory factory,
    WhisperProcessor processor,
    string actualRuntime) : IDisposable
{
    /// <summary>認識に使用するprocessor</summary>
    public WhisperProcessor Processor { get; } = processor;

    /// <summary>Whisper.netが実際にロードしたruntime名</summary>
    public string ActualRuntime { get; } = actualRuntime;

    /// <inheritdoc />
    public void Dispose()
    {
        Processor.Dispose();
        factory.Dispose();
    }
}
