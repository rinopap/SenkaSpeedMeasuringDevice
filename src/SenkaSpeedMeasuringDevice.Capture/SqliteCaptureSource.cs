using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>Polls only allowlisted persisted 74EN response rows; it does not join or change game traffic.</summary>
public sealed class SqliteCaptureSource : ICaptureSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumCoverageGap = TimeSpan.FromSeconds(8);
    private const int CursorFormatVersion = 2;
    private const int MaximumBatchesPerStreamPerPoll = 2;
    private static readonly string[] ApiStreams =
    [
        "api_port/port",
        "api_req_sortie/battleresult",
        "api_req_combined_battle/battleresult",
        "api_req_practice/battle_result",
        "api_req_mission/result"
    ];

    private readonly string _databasePath;
    private readonly string _cursorPath;
    private readonly bool _ownsStore;
    private readonly AdmiralObservationStore _store;
    private readonly SqliteApiFileReader _reader;
    private readonly Func<bool> _is74EnRunning;
    private readonly object _sessionRequestLock = new();
    private CancellationTokenSource? _stopSource;
    private Task? _worker;
    private string? _lastStatus;
    private TaskCompletionSource<string>? _pendingSessionRequest;
    private long _sessionStartAfterId;
    private string? _skipPortBacklogToken;

    public SqliteCaptureSource(string databasePath, string? cursorPath = null,
        AdmiralObservationStore? observationStore = null, Func<bool>? is74EnRunning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _is74EnRunning = is74EnRunning ?? (() => IsElectronicObserverRunning(_databasePath));
        MeasurementSeriesId = string.Empty;
        var pathBytes = Encoding.UTF8.GetBytes(_databasePath.ToUpperInvariant());
        var pathKey = Convert.ToHexString(SHA256.HashData(pathBytes))[..24];
        _cursorPath = cursorPath is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SenkaSpeedMeasuringDevice", $"sqlite-cursor-{pathKey}.json")
            : Path.GetFullPath(cursorPath);
        if (observationStore is null)
        {
            var storePath = _cursorPath + ".journal.sqlite";
            _store = new AdmiralObservationStore(storePath);
            _ownsStore = true;
            _store.ImportLegacyCsvOnce([]);
        }
        else _store = observationStore;
        _reader = new SqliteApiFileReader(_databasePath);
    }

    public string Name => CaptureModule.SqliteSourceName;
    public string MeasurementSessionId { get; private set; } = string.Empty;
    public string MeasurementSeriesId { get; private set; }
    public bool IsRunning => _stopSource is { IsCancellationRequested: false };
    public event EventHandler<ApiResponse>? ResponseReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? MeasurementSeriesChanged;
    public event EventHandler? PortBacklogSkipped;

    public void SkipPortBacklogOnNextStart(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (IsRunning) throw new InvalidOperationException("SQLite取得を開始する前に切替境界を指定してください。");
        _skipPortBacklogToken = token;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRunning) return Task.CompletedTask;
        var stopSource = new CancellationTokenSource();
        _stopSource = stopSource;
        _worker = RunAsync(stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task BeginNewMeasurementSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRunning) throw new InvalidOperationException("SQLite取得を開始してから計測区間を変更してください。");
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sessionRequestLock)
        {
            if (_pendingSessionRequest is not null)
                throw new InvalidOperationException("計測区間の開始処理が進行中です。");
            _pendingSessionRequest = request;
        }
        try { _ = await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            lock (_sessionRequestLock)
                if (ReferenceEquals(_pendingSessionRequest, request)) _pendingSessionRequest = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopSource = _stopSource;
        if (stopSource is null) return;
        _stopSource = null;
        stopSource.Cancel();
        lock (_sessionRequestLock)
        {
            _pendingSessionRequest?.TrySetCanceled(stopSource.Token);
            _pendingSessionRequest = null;
        }
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopSource.IsCancellationRequested) { }
        }
        _worker = null;
        stopSource.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_ownsStore) _store.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var generation = string.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var highWater = await _reader.ReadHighWaterIdAsync(cancellationToken).ConfigureAwait(false);
                var rowFingerprint = await _reader.ReadIdentityFingerprintAsync(cancellationToken).ConfigureAwait(false);
                var currentIdentity = CreateDatabaseIdentity(_databasePath, rowFingerprint);
                var currentGeneration = _store.GetOrCreateGeneration(currentIdentity, highWater);
                var generationChanged = !string.Equals(currentGeneration, generation, StringComparison.Ordinal);
                generation = currentGeneration;
                var sessionRequest = PeekPendingSessionRequest();
                var sessionState = sessionRequest is null
                    ? _store.GetOrCreateActiveMeasurementSession(generation, highWater)
                    : _store.StartNewMeasurementSession(generation, highWater);
                _sessionStartAfterId = sessionState.StartAfterId;
                if (!string.Equals(sessionState.SessionId, MeasurementSessionId, StringComparison.Ordinal))
                {
                    MeasurementSessionId = sessionState.SessionId;
                    UpdateMeasurementSeries(MeasurementSessionId + ":0");
                }
                if (generationChanged) SetStatus("SQLiteの計測区間を復元中");
                if (sessionRequest is not null)
                {
                    lock (_sessionRequestLock)
                        if (ReferenceEquals(_pendingSessionRequest, sessionRequest)) _pendingSessionRequest = null;
                    sessionRequest.TrySetResult(MeasurementSessionId);
                }

                if (Interlocked.Exchange(ref _skipPortBacklogToken, null) is { } skipToken)
                {
                    _store.SkipPortBacklog(generation, MeasurementSessionId, highWater, skipToken);
                    await SaveCursorDiagnosticAsync(generation, MeasurementSessionId, highWater, cancellationToken).ConfigureAwait(false);
                    if (PortBacklogSkipped is { } skippedHandlers)
                        foreach (EventHandler handler in skippedHandlers.GetInvocationList())
                            try { handler(this, EventArgs.Empty); } catch { }
                }

                var allCaughtUp = true;
                var deliveredCount = 0;
                foreach (var stream in ApiStreams)
                {
                    if (stream != "api_port/port" &&
                        (_store.GetCursor(generation, MeasurementSessionId, "api_port/port") ?? -1) < highWater)
                    {
                        allCaughtUp = false;
                        continue;
                    }

                    var cursor = _store.GetCursor(generation, MeasurementSessionId, stream) ?? -1;
                    if (cursor < 0)
                    {
                        cursor = _sessionStartAfterId;
                        var initial = new SqliteApiFileBatch([], [], cursor, 0, 0);
                        if (stream == "api_port/port")
                        {
                            _store.CommitPortBatch(generation, MeasurementSessionId, initial, sessionState.StartedAtUtc);
                            await SaveCursorDiagnosticAsync(generation, MeasurementSessionId, cursor, cancellationToken).ConfigureAwait(false);
                        }
                        else
                            _store.CommitShipGainBatch(generation, MeasurementSessionId, stream, initial);
                    }
                    if (cursor > highWater)
                    {
                        // GetOrCreateGeneration checks the maximum committed ID across every stream.
                        generation = _store.GetOrCreateGeneration(currentIdentity, highWater);
                        sessionState = _store.GetOrCreateActiveMeasurementSession(generation, highWater);
                        MeasurementSessionId = sessionState.SessionId;
                        _sessionStartAfterId = sessionState.StartAfterId;
                        UpdateMeasurementSeries(MeasurementSessionId + ":0");
                        allCaughtUp = false;
                        SetStatus("SQLiteの巻き戻りを検知し、新しい計測区間で再走査中");
                        break;
                    }

                    var streamCaughtUp = false;
                    for (var page = 0; page < MaximumBatchesPerStreamPerPoll && cursor < highWater; page++)
                    {
                        SqliteApiFileBatch batch;
                        if (stream == "api_req_combined_battle/battleresult")
                        {
                            var metadata = await _reader.ReadMetadataBatchAsync(cursor, highWater,
                                SqliteApiFileReader.DefaultMaximumRows, cancellationToken, stream).ConfigureAwait(false);
                            batch = new SqliteApiFileBatch([], metadata.Records.Select(record =>
                                new SqliteApiFileIssue(record.RowId, "out-of-scope-api", record.TimestampUtc)).ToArray(),
                                metadata.ScannedThroughId, 0, 0);
                        }
                        else
                            batch = await _reader.ReadBatchAsync(cursor, highWater,
                                SqliteApiFileReader.DefaultMaximumRows, cancellationToken, stream,
                                sessionState.StartedAtUtc).ConfigureAwait(false);
                        var durableBatch = batch with
                        {
                            Responses = batch.Responses.Select(response => response with
                            {
                                EventId = $"{generation}:{stream}:{response.SourceRecordId}",
                                SourceGenerationId = generation,
                                MeasurementSeriesId = stream == "api_port/port" || response.SourceRecordId is not { } recordId
                                    ? MeasurementSeriesId
                                    : _store.GetMeasurementSeriesForApiRecord(generation, MeasurementSessionId, recordId)
                            }).ToArray(),
                            Issues = batch.Issues.Select(issue => issue with
                            {
                                MeasurementSeriesId = stream == "api_port/port"
                                    ? MeasurementSeriesId
                                    : _store.GetMeasurementSeriesForApiRecord(generation, MeasurementSessionId, issue.RowId)
                            }).ToArray()
                        };

                        if (stream == "api_port/port")
                        {
                            _store.CommitPortBatch(generation, MeasurementSessionId, durableBatch, sessionState.StartedAtUtc);
                            UpdateMeasurementSeries(_store.GetCurrentPortMeasurementSeriesId(generation, MeasurementSessionId));
                        }
                        else
                            _store.CommitShipGainBatch(generation, MeasurementSessionId, stream, durableBatch);
                        cursor = Math.Max(durableBatch.ScannedThroughId,
                            durableBatch.Responses.Select(x => x.SourceRecordId ?? 0).Concat(durableBatch.Issues.Select(x => x.RowId)).DefaultIfEmpty(0).Max());
                        deliveredCount += durableBatch.Responses.Count;
                        if (!Deliver(durableBatch.Responses)) SetStatus("SQLite監視中（画面更新を再試行します）");
                        if (stream == "api_port/port")
                            await SaveCursorDiagnosticAsync(generation, MeasurementSessionId, cursor, cancellationToken).ConfigureAwait(false);
                        if (cursor >= highWater) { streamCaughtUp = true; break; }
                    }

                    if (cursor >= highWater) streamCaughtUp = true;
                    allCaughtUp &= streamCaughtUp;
                }

                if (allCaughtUp && _is74EnRunning())
                {
                    _store.RecordCoverageSuccess(generation, MeasurementSeriesId, DateTimeOffset.UtcNow, MaximumCoverageGap);
                    var outOfScopeCount = _store.GetOutOfScopeEventCount(generation, MeasurementSeriesId);
                    var issueCount = _store.GetIssueCount(generation, MeasurementSeriesId);
                    if (outOfScopeCount > 0)
                        SetStatus($"対応範囲外イベントあり：この計測区間に{outOfScopeCount}件");
                    else if (issueCount > 0)
                        SetStatus($"SQLite監視中：取得に制約があります（{issueCount}件）");
                    else if (deliveredCount > 0) SetStatus("SQLite監視中：結果APIを計測中");
                    else SetStatus("SQLite接続中：対象API待機中");
                }
                else if (allCaughtUp)
                    SetStatus("74EN停止中：保存済み履歴は読取済み（稼働中の計測範囲外）");
                else SetStatus($"SQLite初回履歴を確認中（最大{SqliteApiFileReader.MaximumInitialHistoryIdWindow:N0} ID）");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SqliteException) { SetStatus("SQLite読み取り待機中（DB使用中またはアクセス不可）"); }
            catch (IOException) { SetStatus("SQLite読み取り待機中（ファイルアクセス不可）"); }
            catch (UnauthorizedAccessException) { SetStatus("SQLite読み取り待機中（アクセス権を確認してください）"); }
            catch (InvalidDataException) { SetStatus("SQLite読み取り待機中（DB形式または記録を確認してください）"); }
            catch (JsonException) { SetStatus("SQLite状態を読み取れません。状態DBを確認してください"); }
            catch (InvalidOperationException) { SetStatus("SQLite監視を開始できません。対象DBの形式を確認してください"); }

            try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    private bool Deliver(IReadOnlyList<ApiResponse> responses)
    {
        var handlers = ResponseReceived?.GetInvocationList();
        if (handlers is null || handlers.Length == 0) return true;
        var success = true;
        foreach (var response in responses)
        foreach (EventHandler<ApiResponse> handler in handlers)
        {
            try { handler(this, response); }
            catch { success = false; }
        }
        return success;
    }

    private async Task SaveCursorDiagnosticAsync(string generation, string seriesId, long cursor, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cursorPath) ?? throw new InvalidOperationException("Cursor path must have a directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _cursorPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new CursorState(CursorFormatVersion, generation, seriesId, cursor));
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _cursorPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("SQLite監視中（カーソル診断ファイルを書き込めません。内部記録は継続中）");
        }
        finally { try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch (IOException) { } }
    }

    private static string CreateDatabaseIdentity(string path, string rowFingerprint)
    {
        var creationTicks = File.Exists(path) ? File.GetCreationTimeUtc(path).Ticks : 0;
        var fileId = TryGetWindowsFileId(path) ?? "unavailable";
        var identity = Encoding.UTF8.GetBytes($"{path.ToUpperInvariant()}\n{creationTicks}\n{fileId}\n{rowFingerprint}");
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private static bool IsElectronicObserverRunning(string databasePath)
    {
        var recordDirectory = Path.GetDirectoryName(databasePath);
        var installDirectory = recordDirectory is null ? null : Directory.GetParent(recordDirectory)?.FullName;
        if (installDirectory is null) return false;
        foreach (var process in Process.GetProcessesByName("ElectronicObserver"))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null) continue;
                    var actualDirectory = Path.GetDirectoryName(Path.GetFullPath(executable));
                    if (string.Equals(actualDirectory, installDirectory, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // An inaccessible unrelated process is not evidence that the selected 74EN instance is running.
                }
            }
        }
        return false;
    }

    private static string? TryGetWindowsFileId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = CreateFile(path, 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_lastStatus, status, StringComparison.Ordinal)) return;
        _lastStatus = status;
        if (StatusChanged is not { } handlers) return;
        foreach (EventHandler<string> handler in handlers.GetInvocationList()) { try { handler(this, status); } catch { } }
    }

    private void UpdateMeasurementSeries(string seriesId)
    {
        if (string.Equals(MeasurementSeriesId, seriesId, StringComparison.Ordinal)) return;
        MeasurementSeriesId = seriesId;
        if (MeasurementSeriesChanged is not { } handlers) return;
        foreach (EventHandler<string> handler in handlers.GetInvocationList()) { try { handler(this, seriesId); } catch { } }
    }

    private TaskCompletionSource<string>? PeekPendingSessionRequest()
    {
        lock (_sessionRequestLock) return _pendingSessionRequest;
    }

    private sealed record CursorState(int FormatVersion, string GenerationId, string SeriesId, long LastId);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
