using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiCore.Tests.Infrastructure;

/// <summary>
/// Records every log entry at every level, including the full text of any attached exception, so tests can
/// assert that sensitive values never reach logs, whichever part of the stack writes them.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    public sealed record LogEntry(string Category, LogLevel Level, string Message, string? Exception,
        IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<string> Scopes)
    {
        public bool Contains(string value) =>
            Message.Contains(value, StringComparison.Ordinal) ||
            (Exception?.Contains(value, StringComparison.Ordinal) ?? false) ||
            Fields.Any(f => f.Key.Contains(value, StringComparison.Ordinal) ||
                (f.Value?.ToString()?.Contains(value, StringComparison.Ordinal) ?? false)) ||
            Scopes.Any(s => s.Contains(value, StringComparison.Ordinal));
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(k => k.Key, v => v.Value) : new Dictionary<string, object?>();
            var scopes = new List<string>();
            owner._scopes.ForEachScope((scope, list) =>
            {
                list.Add(scope?.ToString() ?? "");
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    list.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
            }, scopes);
            owner._entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception?.ToString(), fields, scopes));
        }
    }
}
