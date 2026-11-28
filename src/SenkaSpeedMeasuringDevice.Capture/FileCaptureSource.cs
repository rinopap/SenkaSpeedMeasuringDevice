using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Observes 74EN's saved response files. It never opens a network connection.</summary>
public sealed class FileCaptureSource : ICaptureSource
{
    private const string TargetName = "api_port@port";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private Channel<string> _queue = CreateQueue();
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _seenSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _eventIds = new(StringComparer.Ordinal);
    private readonly object _eventIdsLock = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _stopSource;
    private Task? _worker;
    private Task? _scanner;

    public FileCaptureSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string Name => CaptureModule.JsonSourceName;
    public bool IsRunning => _stopSource is { IsCancellationRequested: false };
    public event EventHandler<ApiResponse>? ResponseReceived;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning) return Task.CompletedTask;

        Directory.CreateDirectory(_directory);
        _seenSignatures.Clear();
        foreach (var path in EnumerateTargetFiles())
            _seenSignatures[path] = GetSignature(path);

        var stopSource = new CancellationTokenSource();
        _stopSource = stopSource;
        _queued.Clear();
        _queue = CreateQueue();
        _watcher = new FileSystemWatcher(_directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.json",
            EnableRaisingEvents = false
        };
        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
        ScanAndQueueChangedFiles();
        _worker = ProcessQueueAsync(stopSource.Token);
        _scanner = ScanAsync(stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopSource = _stopSource;
        if (stopSource is null) return;
        _stopSource = null;
        stopSource.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        _queue.Writer.TryComplete();

        var running = new[] { _worker, _scanner }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stopSource.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
        finally
        {
            _worker = null;
            _scanner = null;
            _queued.Clear();
            stopSource.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => QueueIfTarget(args.FullPath);
    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        if (IsTarget(args.OldFullPath)) _seenSignatures.TryRemove(args.OldFullPath, out _);
        QueueIfTarget(args.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) => ScanAndQueueChangedFiles();

    private void QueueIfTarget(string path)
    {
        if (!IsTarget(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!_queued.TryAdd(fullPath, 0)) return;
        if (!_queue.Writer.TryWrite(fullPath)) _queued.TryRemove(fullPath, out _);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await ProcessFileAsync(path, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { _queued.TryRemove(path, out _); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return;
            var before = GetSignature(path);
            if (_seenSignatures.TryGetValue(path, out var previous) && previous == before) return;

            byte[] body;
            try { body = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (IOException) when (attempt < 7)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);
            if (!File.Exists(path) || GetSignature(path) != before)
            {
                if (attempt < 7) continue;
                return;
            }

            var timestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var eventId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(timestamp.Ticks + ":").Concat(body).ToArray()));
            _seenSignatures[path] = before;
            var response = new ApiResponse("/kcsapi/api_port/port", timestamp, body, eventId);
            if (ApiResponseParser.Parse(response) is null) return;
            if (!RememberEvent(eventId)) return;
            if (ResponseReceived is { } handlers)
            {
                foreach (EventHandler<ApiResponse> handler in handlers.GetInvocationList())
                {
                    try { handler(this, response); }
                    catch { /* A consumer error must not stop filesystem observation. */ }
                }
            }
            return;
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            ScanAndQueueChangedFiles();
        }
    }

    private void ScanAndQueueChangedFiles()
    {
        try
        {
            foreach (var path in EnumerateTargetFiles())
            {
                var signature = GetSignature(path);
                if (!_seenSignatures.TryGetValue(path, out var prior) || prior != signature)
                    QueueIfTarget(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private IEnumerable<string> EnumerateTargetFiles()
    {
        if (!Directory.Exists(_directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            if (IsTarget(path)) yield return path;
    }

    private static bool IsTarget(string path) =>
        Path.GetFileName(path).Contains(TargetName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private static string GetSignature(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private bool RememberEvent(string eventId)
    {
        lock (_eventIdsLock)
        {
            if (!_eventIds.Add(eventId)) return false;
            if (_eventIds.Count > 4096) _eventIds.Remove(_eventIds.First());
            return true;
        }
    }

    private static Channel<string> CreateQueue() => Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
}
