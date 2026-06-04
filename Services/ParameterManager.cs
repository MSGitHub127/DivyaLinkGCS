// Services/ParameterManager.cs
// The core parameter protocol engine for DivyaLink.
// Replicates the MAVLink parameter list logic found in Mission Planner's
// MAVLinkInterface.cs (getParamList / setParam / verifyParamChange).
//
// PROTOCOL SUMMARY
// ─────────────────────────────────────────────────────────────────────────
// DOWNLOAD (bulk):
//   GCS → PARAM_REQUEST_LIST
//   FC  → PARAM_VALUE × N  (each carries param_index and param_count)
//   GCS detects gaps (missing indices) after stream goes quiet
//   GCS → PARAM_REQUEST_READ per missing index (retry up to MaxRetryRounds)
//   On completion: all N params received → fire OnDownloadComplete
//
// WRITE (single):
//   GCS → PARAM_SET (param_id, param_value, param_type)
//   FC  → PARAM_VALUE echo with same param_id
//   GCS verifies echo, updates local store, fires OnWriteConfirmed
//   Retry up to MaxWriteRetries on timeout (3 s each)
//   IMPORTANT: Only one write in-flight at a time (single-inflight rule).
//
// THREAD SAFETY
//   HandleParamValue() is called from the MavlinkService reader thread.
//   All public methods are safe to call from the Blazor UI thread.
//   Internal state uses ConcurrentDictionary + lock-free primitives where possible.
//   The write queue uses a SemaphoreSlim(1,1) to enforce single-inflight.

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using BlazorApp3.Models;
using static MAVLink;

namespace BlazorApp3.Services;

// ── Download-state enum ────────────────────────────────────────────────────

public enum DownloadState
{
    Idle,
    Requesting,    // PARAM_REQUEST_LIST sent, waiting for first PARAM_VALUE
    Downloading,   // Receiving parameters
    Retrying,      // Filling gaps with PARAM_REQUEST_READ
    Complete,      // All parameters received
    Partial,       // Gave up after max retries — some parameters missing
    Error          // Transport or timeout error during download
}

// ── Event argument types ──────────────────────────────────────────────────

public sealed record DownloadProgressArgs(int Received, int Total)
{
    public float Percent => Total > 0 ? Received * 100f / Total : 0f;
}

public sealed record WriteResultArgs(
    string ParamId,
    float  ConfirmedValue,
    bool   Success,
    string? ErrorMessage = null);

// ── ParameterManager ──────────────────────────────────────────────────────

public sealed class ParameterManager
{
    // ── Tuneable constants ────────────────────────────────────────────────

    /// <summary>
    /// How long to wait with no new packets before triggering a gap-fill retry.
    /// 2 s is generous for SiK 57600-baud wireless links.
    /// </summary>
    private static readonly TimeSpan QuietTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Maximum rounds of gap-fill retries before giving up.</summary>
    private const int MaxRetryRounds = 5;

    /// <summary>Per-round timeout waiting for requested missing params.</summary>
    private static readonly TimeSpan RetryRoundTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Per-attempt timeout for a single PARAM_SET echo.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Maximum re-sends of a PARAM_SET before declaring write failure.</summary>
    private const int MaxWriteRetries = 3;

    /// <summary>
    /// UI progress notification is throttled — fire at most once per this interval.
    /// Prevents flooding the Blazor SignalR channel with 750+ StateHasChanged calls.
    /// </summary>
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(200);

    // ── Dependencies ──────────────────────────────────────────────────────

    private readonly MavlinkService _mavlink;
    private readonly ParameterMetadataService _metadata;
    private readonly ILogger<ParameterManager> _log;

    // ── Parameter store ───────────────────────────────────────────────────

    /// <summary>
    /// Live parameter store.  Thread-safe via ConcurrentDictionary.
    /// Key = param name (upper-case).
    /// </summary>
    private readonly ConcurrentDictionary<string, ParameterEntry> _params = new(
        StringComparer.OrdinalIgnoreCase);

    // ── Download-tracking state ───────────────────────────────────────────

    private volatile DownloadState _state = DownloadState.Idle;
    private int _expectedCount = 0;
    private DateTime _lastParamReceivedUtc = DateTime.MinValue;
    private DateTime _lastProgressNotifyUtc = DateTime.MinValue;

    /// <summary>
    /// Bit-array tracking which param_index values have been received.
    /// Allocated once param_count is known from the first PARAM_VALUE.
    /// Protected by _trackingLock for safe resize + BitArray access.
    /// </summary>
    private bool[]? _received;
    private readonly object _trackingLock = new();

    // ── Write-queue state ─────────────────────────────────────────────────

    /// <summary>Single-inflight gate — only one PARAM_SET in flight at a time.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Pending write awaiting an echo PARAM_VALUE.  Protected by _writeLock.</summary>
    private PendingWrite? _pendingWrite;

    private sealed class PendingWrite(
        string paramId,
        float  value,
        MAV_PARAM_TYPE paramType,
        TaskCompletionSource<ParameterEntry> tcs)
    {
        public string ParamId     { get; } = paramId;
        public float  Value       { get; } = value;
        public MAV_PARAM_TYPE ParamType { get; } = paramType;
        public TaskCompletionSource<ParameterEntry> Tcs { get; } = tcs;
        public int Attempts       { get; set; }
    }

    // ── Events ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired periodically during download.  Throttled to ~5 Hz.
    /// Safe to call InvokeAsync(StateHasChanged) inside the handler.
    /// </summary>
    public event Action<DownloadProgressArgs>? OnProgressChanged;

    /// <summary>Fired once when all parameters are received without gaps.</summary>
    public event Action<IReadOnlyDictionary<string, ParameterEntry>>? OnDownloadComplete;

    /// <summary>Fired when download finishes but some parameters are still missing.</summary>
    public event Action<DownloadProgressArgs>? OnDownloadPartial;

    /// <summary>
    /// Fired when any PARAM_VALUE is received during normal telemetry
    /// (outside of a bulk download), so the UI can update a displayed value live.
    /// </summary>
    public event Action<ParameterEntry>? OnParameterLiveUpdate;

    /// <summary>Fired when a PARAM_SET write is confirmed by the FC echo.</summary>
    public event Action<WriteResultArgs>? OnWriteConfirmed;

    /// <summary>Fired when a PARAM_SET write fails after all retries.</summary>
    public event Action<WriteResultArgs>? OnWriteFailed;

    // ── Public read-only state ────────────────────────────────────────────

    public DownloadState State => _state;

    public int ReceivedCount  => _params.Count;
    public int ExpectedCount  => _expectedCount;
    public float DownloadPercent =>
        _expectedCount > 0 ? _params.Count * 100f / _expectedCount : 0f;

    /// <summary>
    /// Returns a thread-safe snapshot of the current parameter dictionary.
    /// Snapshot is point-in-time — iterating it is safe even while HandleParamValue
    /// is updating _params on another thread.
    /// </summary>
    public IReadOnlyDictionary<string, ParameterEntry> Parameters =>
        (IReadOnlyDictionary<string, ParameterEntry>)_params;

    /// <summary>
    /// Returns a sorted, point-in-time list suitable for UI rendering.
    /// Sorted by <see cref="ParameterEntry.Index"/> (FC-native order).
    /// </summary>
    public IReadOnlyList<ParameterEntry> GetSortedList() =>
        [.. _params.Values.OrderBy(p => p.Index)];

    public string MetadataStatus => _metadata.MetadataStatus;
    
    // ── Constructor ───────────────────────────────────────────────────────

    public ParameterManager(
        MavlinkService mavlink,
        ParameterMetadataService metadata,
        ILogger<ParameterManager> log)
    {
        _mavlink  = mavlink;
        _metadata = metadata;
        _log      = log;
    }

    // ── Download ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates a full parameter download from the connected flight controller.
    /// Returns true when all parameters are received, false on partial/error.
    ///
    /// Call from a "Download Parameters" button click handler.
    /// Fires <see cref="OnProgressChanged"/> periodically during download.
    /// Fires <see cref="OnDownloadComplete"/> or <see cref="OnDownloadPartial"/> on finish.
    /// </summary>
    public async Task<bool> RequestAllParametersAsync(
        ArduPilotVehicleType vehicleType = ArduPilotVehicleType.ArduCopter,
        CancellationToken ct = default)
    {
        if (_state is DownloadState.Downloading or DownloadState.Requesting or DownloadState.Retrying)
        {
            _log.LogWarning("RequestAllParameters called while already in state {State}", _state);
            return false;
        }

        // ── Reset state ────────────────────────────────────────────────────
        _params.Clear();
        _expectedCount = 0;
        _lastParamReceivedUtc = DateTime.UtcNow;

        lock (_trackingLock) { _received = null; }

        _state = DownloadState.Requesting;
        _log.LogInformation("Requesting full parameter list from FC");

        // ── Send PARAM_REQUEST_LIST ────────────────────────────────────────
        if (!_mavlink.HasVehicle)
        {
            _log.LogError("No vehicle connected — cannot request parameters");
            _state = DownloadState.Error;
            return false;
        }

        _mavlink.SendParamRequestList();

        // ── Wait for completion with watchdog ──────────────────────────────
        var success = await WaitForDownloadAsync(ct);

        // ── Annotate all entries with metadata ─────────────────────────────
        _log.LogInformation("Annotating {Count} parameters with ArduPilot metadata", _params.Count);
        await _metadata.AnnotateAsync(_params.Values, vehicleType, ct);

        // ── Fire completion events ─────────────────────────────────────────
        if (success)
        {
            _state = DownloadState.Complete;
            _log.LogInformation("Parameter download complete: {Count} parameters", _params.Count);
            OnDownloadComplete?.Invoke(Parameters);
        }
        else
        {
            _state = DownloadState.Partial;
            var args = new DownloadProgressArgs(_params.Count, _expectedCount);
            _log.LogWarning("Parameter download partial: {Received}/{Total}", _params.Count, _expectedCount);
            OnDownloadPartial?.Invoke(args);
        }

        return success;
    }

    /// <summary>
    /// Watchdog loop: waits for the PARAM_VALUE stream to complete or go quiet,
    /// then fills gaps via PARAM_REQUEST_READ.
    /// </summary>
    private async Task<bool> WaitForDownloadAsync(CancellationToken ct)
    {
        _state = DownloadState.Downloading;

        // Wait until we know the total count (first PARAM_VALUE arrives)
        var waitStart = DateTime.UtcNow;
        while (_expectedCount == 0 && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            if (DateTime.UtcNow - waitStart > TimeSpan.FromSeconds(10))
            {
                _log.LogError("Timed out waiting for first PARAM_VALUE from FC");
                _state = DownloadState.Error;
                return false;
            }
        }

        // Poll for stream completion
        for (int round = 0; round < MaxRetryRounds; round++)
        {
            // Wait until no new packet for QuietTimeout
            while (DateTime.UtcNow - _lastParamReceivedUtc < QuietTimeout
                   && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
            }

            if (ct.IsCancellationRequested) return false;

            // Check if complete
            var missing = GetMissingIndices();
            if (missing.Count == 0)
            {
                _log.LogInformation("All {Count} parameters received after {Round} retry round(s)",
                    _params.Count, round);
                return true;
            }

            _log.LogWarning("Round {Round}: {Missing} parameters missing — requesting individually",
                round + 1, missing.Count);

            _state = DownloadState.Retrying;

            // Request each missing index
            foreach (var idx in missing)
            {
                if (ct.IsCancellationRequested) return false;
                _mavlink.SendParamRequestRead(idx, _mavlink.State.SystemId, _mavlink.State.ComponentId);
                await Task.Delay(50, ct); // 20 req/s — safe for SiK radios
            }

            // Reset quiet timer for the retry responses
            _lastParamReceivedUtc = DateTime.UtcNow;
            await Task.Delay(RetryRoundTimeout, ct);

            _state = DownloadState.Downloading;
        }

        // Final check after all retry rounds
        return GetMissingIndices().Count == 0;
    }

    /// <summary>
    /// Returns all param_index values not yet received.
    /// Thread-safe via _trackingLock.
    /// </summary>
    private List<int> GetMissingIndices()
    {
        var missing = new List<int>();
        lock (_trackingLock)
        {
            if (_received == null || _expectedCount == 0) return missing;
            for (int i = 0; i < _expectedCount; i++)
                if (!_received[i]) missing.Add(i);
        }
        return missing;
    }

    // ── Incoming PARAM_VALUE handler ──────────────────────────────────────

    /// <summary>
    /// Called by MavlinkService for EVERY incoming PARAM_VALUE message.
    /// Thread-safe — called from the MAVLink reader thread.
    /// </summary>
    public void HandleParamValue(mavlink_param_value_t raw)
    {
        // Decode null-terminated ASCII name
        var name = Encoding.ASCII
            .GetString(raw.param_id)
            .TrimEnd('\0', ' ')
            .ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(name)) return;

        var now = DateTime.UtcNow;

        // ── Update or create the parameter entry ─────────────────────────
        var entry = _params.AddOrUpdate(
            name,
            // Add path: new entry
            _ => new ParameterEntry
            {
                Id             = name,
                Value          = raw.param_value,
                OriginalValue  = raw.param_value,
                Index          = raw.param_index,
                TotalCount     = raw.param_count,
                ParamType      = (MAV_PARAM_TYPE)raw.param_type,
                LastUpdatedUtc = now
            },
            // Update path: existing entry (live update or gap-fill)
            (_, existing) =>
            {
                existing.Value          = raw.param_value;
                existing.Index          = raw.param_index;
                existing.TotalCount     = raw.param_count;
                existing.LastUpdatedUtc = now;
                return existing;
            }
        );

        // ── Mark index as received ────────────────────────────────────────
        lock (_trackingLock)
        {
            // Initialise tracking array on first packet
            if (_received == null && raw.param_count > 0)
            {
                _expectedCount = raw.param_count;
                _received      = new bool[raw.param_count];
                _log.LogInformation("FC reported {Total} total parameters", _expectedCount);
            }

            if (_received != null && raw.param_index < _received.Length)
                _received[raw.param_index] = true;
        }

        _lastParamReceivedUtc = now;

        // ── Resolve a pending write (echo detection) ───────────────────────
        var pending = Volatile.Read(ref _pendingWrite);
        if (pending != null &&
            string.Equals(pending.ParamId, name, StringComparison.OrdinalIgnoreCase))
        {
            entry.CommitWrite();
            Volatile.Write(ref _pendingWrite, null);
            pending.Tcs.TrySetResult(entry);
        }

        // ── Throttled UI progress notification ───────────────────────────
        if (_state is DownloadState.Downloading or DownloadState.Retrying)
        {
            if (now - _lastProgressNotifyUtc >= ProgressThrottle)
            {
                _lastProgressNotifyUtc = now;
                var args = new DownloadProgressArgs(_params.Count, _expectedCount);
                // Fire on thread pool — caller must marshal to circuit if needed
                _ = Task.Run(() => OnProgressChanged?.Invoke(args));
            }
        }
        else if (_state == DownloadState.Complete)
        {
            // Live update while not in a download — notify so UI can refresh a value
            _ = Task.Run(() => OnParameterLiveUpdate?.Invoke(entry));
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a single parameter to the FC and waits for the echo confirmation.
    ///
    /// Returns the confirmed <see cref="ParameterEntry"/> on success.
    /// Throws <see cref="TimeoutException"/> after all retries are exhausted.
    ///
    /// SINGLE-INFLIGHT: This method will wait if another write is already in progress.
    /// </summary>
    public async Task<ParameterEntry> WriteParameterAsync(
        string paramId,
        float  value,
        CancellationToken ct = default)
    {
        paramId = paramId.ToUpperInvariant();

        if (!_params.TryGetValue(paramId, out var entry))
            throw new KeyNotFoundException(
                $"Parameter '{paramId}' not found in local store. " +
                "Download parameters first.");

        if (!entry.IsInRange && (entry.RangeMin.HasValue || entry.RangeMax.HasValue))
        {
            // Non-blocking warning — caller decides whether to proceed
            _log.LogWarning("Value {Value} for {Id} is outside documented range [{Min}, {Max}]",
                value, paramId, entry.RangeMin, entry.RangeMax);
        }

        // Acquire the single-inflight gate
        await _writeLock.WaitAsync(ct);
        try
        {
            entry.IsWritePending = true;

            for (int attempt = 0; attempt < MaxWriteRetries; attempt++)
            {
                var tcs = new TaskCompletionSource<ParameterEntry>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _pendingWrite = new PendingWrite(paramId, value, entry.ParamType, tcs)
                {
                    Attempts = attempt + 1
                };

                _log.LogDebug("Writing {Id} = {Value} (attempt {Attempt}/{Max})",
                    paramId, value, attempt + 1, MaxWriteRetries);

                // Optimistically update local value so UI reflects the change immediately
                entry.Value = value;

                _mavlink.SendParameter(paramId, value);

                using var timeoutCts = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(WriteTimeout);

                try
                {
                    var confirmed = await tcs.Task.WaitAsync(timeoutCts.Token);
                    _log.LogInformation("Write confirmed: {Id} = {Value}", paramId, confirmed.Value);
                    var args = new WriteResultArgs(paramId, confirmed.Value, Success: true);
                    OnWriteConfirmed?.Invoke(args);
                    return confirmed;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout on this attempt — retry
                    Volatile.Write(ref _pendingWrite, null);
                    _log.LogWarning("Write timeout for {Id} (attempt {Attempt})", paramId, attempt + 1);
                }
            }

            // All retries exhausted
            entry.IsWritePending = false;
            // Revert local value to the last known good value
            entry.Value = entry.OriginalValue;

            var failArgs = new WriteResultArgs(paramId, entry.OriginalValue, Success: false,
                ErrorMessage: $"No echo after {MaxWriteRetries} attempts");
            OnWriteFailed?.Invoke(failArgs);

            throw new TimeoutException(
                $"PARAM_SET for '{paramId}' was not confirmed after {MaxWriteRetries} attempts.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes multiple parameters sequentially with a configurable inter-write delay.
    /// Spacing of 200 ms is safe for SiK 57600-baud wireless links.
    ///
    /// Returns a list of (paramId, success, confirmedValue/errorMessage) tuples.
    /// </summary>
    public async Task<IReadOnlyList<WriteResultArgs>> WriteParametersBatchAsync(
        IEnumerable<(string paramId, float value)> writes,
        TimeSpan? interWriteDelay = null,
        CancellationToken ct = default)
    {
        var delay   = interWriteDelay ?? TimeSpan.FromMilliseconds(200);
        var results = new List<WriteResultArgs>();

        foreach (var (paramId, value) in writes)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var entry = await WriteParameterAsync(paramId, value, ct);
                results.Add(new WriteResultArgs(paramId, entry.Value, Success: true));
            }
            catch (Exception ex)
            {
                results.Add(new WriteResultArgs(paramId, 0f, Success: false,
                    ErrorMessage: ex.Message));
            }

            await Task.Delay(delay, ct);
        }

        return results;
    }

    // ── Utility ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current value of a parameter by name, or null if not downloaded.
    /// </summary>
    public float? GetValue(string paramId) =>
        _params.TryGetValue(paramId, out var e) ? e.Value : null;

    /// <summary>
    /// Clears the local parameter store and resets download state.
    /// Call before re-connecting to a different FC.
    /// </summary>
    public void Reset()
    {
        _params.Clear();
        _expectedCount = 0;
        _state = DownloadState.Idle;
        lock (_trackingLock) { _received = null; }
        _log.LogInformation("ParameterManager reset");
    }
}
