using System.IO;
using Microsoft.Extensions.Logging;

namespace VoxArchive.Wpf;

internal sealed class AppFileLoggerProvider : ILoggerProvider
{
    private readonly string _logPath;
    private readonly object _sync = new();

    public AppFileLoggerProvider(string logPath)
    {
        _logPath = logPath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new AppFileLogger(categoryName, _logPath, _sync);
    }

    public void Dispose()
    {
    }

    private sealed class AppFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logPath;
        private readonly object _sync;

        public AppFileLogger(string categoryName, string logPath, object sync)
        {
            _categoryName = categoryName;
            _logPath = logPath;
            _sync = sync;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_categoryName}] {message}";

            try
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                lock (_sync)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                    if (exception is not null)
                    {
                        File.AppendAllText(_logPath, exception + Environment.NewLine);
                    }
                }
            }
            catch
            {
                // ファイルログ失敗でアプリを止めない。
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

