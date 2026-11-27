using System.Text.Json;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Capture;

/// <summary>
/// Enforces the application's fixed, observation-only CDP command surface and current page generation.
/// It is deliberately not a general purpose CDP client policy.
/// </summary>
public sealed class CdpCommandPolicy
{
    public const int MaximumBufferedBytes = 8 * 1024 * 1024;
    private const int MaximumAttachedFrames = 64;
    private readonly object _gate = new();
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal) { string.Empty };
    private readonly HashSet<string> _relatedFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionParents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string RequestId), long> _requests = new();
    private long _generation = 1;
    private string? _topFrameId;
    private bool _pageAllowed = true;

    public long Generation { get { lock (_gate) return _generation; } }
    public bool IsPageAllowed { get { lock (_gate) return _pageAllowed; } }
    public bool IsSessionActive(string? sessionId)
    {
        lock (_gate) return _sessions.Contains(sessionId ?? string.Empty);
    }

    public void SetTopFrame(string frameId, Uri url, IEnumerable<(string FrameId, string? ParentFrameId)>? relatedFrames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        lock (_gate)
        {
            _topFrameId = frameId;
            _pageAllowed = KancolleCapturePolicy.IsAllowedGamePage(url);
            _relatedFrames.Clear();
            _frameParents.Clear();
            _relatedFrames.Add(frameId);
            if (relatedFrames is not null)
                foreach (var relatedFrame in relatedFrames.Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId)))
                {
                    _relatedFrames.Add(relatedFrame.FrameId);
                    if (!string.IsNullOrWhiteSpace(relatedFrame.ParentFrameId))
                        _frameParents[relatedFrame.FrameId] = relatedFrame.ParentFrameId!;
                }
            if (!_pageAllowed) InvalidateLocked();
        }
    }

    public void ObserveEvent(JsonElement message)
    {
        if (!message.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method ||
            !message.TryGetProperty("params", out var parameters)) return;

        var outerSession = message.TryGetProperty("sessionId", out var outer) ? outer.GetString() ?? string.Empty : string.Empty;
        lock (_gate)
        {
            if (method == "Target.attachedToTarget" && _pageAllowed && _sessions.Contains(outerSession) &&
                parameters.TryGetProperty("sessionId", out var child) && child.GetString() is { Length: > 0 } childId &&
                parameters.TryGetProperty("targetInfo", out var info) &&
                info.TryGetProperty("type", out var type) && type.GetString() == "iframe" &&
                info.TryGetProperty("parentFrameId", out var parentFrame) && parentFrame.GetString() is { } parentFrameId &&
                _relatedFrames.Contains(parentFrameId))
            {
                if (_sessions.Count < MaximumAttachedFrames + 1)
                {
                    _sessions.Add(childId);
                    _sessionParents[childId] = parentFrameId;
                }
                return;
            }

            if (method == "Target.detachedFromTarget" && parameters.TryGetProperty("sessionId", out var detached) &&
                detached.GetString() is { } detachedId)
            {
                RemoveSessionLocked(detachedId);
                return;
            }

            if (method == "Page.frameAttached" && _pageAllowed && _sessions.Contains(outerSession) &&
                parameters.TryGetProperty("frameId", out var attachedId) && attachedId.GetString() is { Length: > 0 } frameId &&
                parameters.TryGetProperty("parentFrameId", out var parentId) && parentId.GetString() is { } parentIdText &&
                _relatedFrames.Contains(parentIdText))
            {
                _relatedFrames.Add(frameId);
                _frameParents[frameId] = parentIdText;
                return;
            }

            if (method == "Page.frameDetached" && _sessions.Contains(outerSession) &&
                parameters.TryGetProperty("frameId", out var detachedFrame) && detachedFrame.GetString() is { } detachedFrameId)
            {
                RemoveFrameLocked(detachedFrameId);
                return;
            }

            if (outerSession.Length == 0 && TryGetTopNavigationUrl(method, parameters, out var url))
            {
                _generation++;
                _pageAllowed = KancolleCapturePolicy.IsAllowedGamePage(url);
                _requests.Clear();
                if (!_pageAllowed)
                {
                    _sessions.RemoveWhere(s => s.Length > 0);
                    _sessionParents.Clear();
                    _relatedFrames.Clear();
                    _frameParents.Clear();
                    _relatedFrames.Add(_topFrameId!);
                }
            }
        }
    }

    private bool TryGetTopNavigationUrl(string method, JsonElement parameters, out Uri url)
    {
        url = new Uri("about:blank");
        if (method == "Page.frameNavigated" && parameters.TryGetProperty("frame", out var frame) &&
            frame.TryGetProperty("id", out var id) && id.GetString() == _topFrameId &&
            frame.TryGetProperty("url", out var frameUrl))
        {
            if (Uri.TryCreate(frameUrl.GetString(), UriKind.Absolute, out var parsed)) { url = parsed; return true; }
            return false;
        }
        if (method == "Page.navigatedWithinDocument" && parameters.TryGetProperty("frameId", out var frameId) &&
            frameId.GetString() == _topFrameId && parameters.TryGetProperty("url", out var sameDocumentUrl))
        {
            if (Uri.TryCreate(sameDocumentUrl.GetString(), UriKind.Absolute, out var parsed)) { url = parsed; return true; }
            return false;
        }
        return false;
    }

    public bool TryRegisterRequest(string? sessionId, string requestId, long observedGeneration, out long generation)
    {
        generation = 0;
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        lock (_gate)
        {
            var session = sessionId ?? string.Empty;
            if (!_pageAllowed || _generation != observedGeneration || !_sessions.Contains(session)) return false;
            generation = _generation;
            _requests[(session, requestId)] = generation;
            if (_requests.Count > 256) _requests.Remove(_requests.Keys.First());
            return true;
        }
    }

    public bool IsCurrent(string? sessionId, string requestId, long generation)
    {
        lock (_gate)
            return _pageAllowed && _generation == generation &&
                   _requests.TryGetValue((sessionId ?? string.Empty, requestId), out var recorded) && recorded == generation;
    }

    public bool TryExecuteIfCurrent(string? sessionId, string requestId, long generation, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!_pageAllowed || _generation != generation ||
                !_requests.TryGetValue((sessionId ?? string.Empty, requestId), out var recorded) || recorded != generation)
                return false;
            action();
            return true;
        }
    }

    public void CompleteRequest(string? sessionId, string requestId)
    {
        lock (_gate) _requests.Remove((sessionId ?? string.Empty, requestId));
    }

    public bool ValidateCommand(string method, JsonElement parameters, string? sessionId)
    {
        lock (_gate)
        {
            var session = sessionId ?? string.Empty;
            if (!_sessions.Contains(session)) return false;
            return method switch
            {
                "Network.enable" => IsNetworkEnable(parameters),
                "Network.disable" => IsEmpty(parameters),
                "Network.getResponseBody" => IsResponseBody(parameters, session),
                "Target.setAutoAttach" => IsFixedAutoAttach(parameters, session),
                "Page.enable" or "Page.disable" or "Page.getFrameTree" => IsEmpty(parameters),
                _ => false
            };
        }
    }

    private bool IsResponseBody(JsonElement parameters, string session)
    {
        if (!OnlyProperties(parameters, "requestId") || !parameters.TryGetProperty("requestId", out var id) ||
            id.ValueKind != JsonValueKind.String) return false;
        return _pageAllowed && _requests.TryGetValue((session, id.GetString()!), out var generation) && generation == _generation;
    }

    private static bool IsNetworkEnable(JsonElement parameters)
    {
        return OnlyProperties(parameters, "maxTotalBufferSize", "maxResourceBufferSize") &&
               parameters.TryGetProperty("maxTotalBufferSize", out var total) && total.TryGetInt32(out var totalValue) &&
               parameters.TryGetProperty("maxResourceBufferSize", out var resource) && resource.TryGetInt32(out var resourceValue) &&
               totalValue == MaximumBufferedBytes && resourceValue == MaximumBufferedBytes;
    }

    private static bool IsFixedAutoAttach(JsonElement parameters, string session)
    {
        if (parameters.TryGetProperty("autoAttach", out var disabled) && disabled.ValueKind == JsonValueKind.False)
            return session.Length == 0 && OnlyProperties(parameters, "autoAttach", "waitForDebuggerOnStart", "flatten") &&
                   parameters.TryGetProperty("waitForDebuggerOnStart", out var noPause) && noPause.ValueKind == JsonValueKind.False &&
                   parameters.TryGetProperty("flatten", out var flat) && flat.ValueKind == JsonValueKind.True;

        if (!OnlyProperties(parameters, "autoAttach", "waitForDebuggerOnStart", "flatten", "filter") ||
            !parameters.TryGetProperty("autoAttach", out var autoAttach) || autoAttach.ValueKind != JsonValueKind.True ||
            !parameters.TryGetProperty("waitForDebuggerOnStart", out var pause) || pause.ValueKind != JsonValueKind.False ||
            !parameters.TryGetProperty("flatten", out var flatten) || flatten.ValueKind != JsonValueKind.True ||
            !parameters.TryGetProperty("filter", out var filter) || filter.ValueKind != JsonValueKind.Array || filter.GetArrayLength() != 2)
            return false;

        var entries = filter.EnumerateArray().ToArray();
        return OnlyProperties(entries[0], "type", "exclude") &&
               entries[0].TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "iframe" &&
               entries[0].TryGetProperty("exclude", out var include) && include.ValueKind == JsonValueKind.False &&
               OnlyProperties(entries[1], "exclude") && entries[1].TryGetProperty("exclude", out var exclude) &&
               exclude.ValueKind == JsonValueKind.True;
    }

    private static bool IsEmpty(JsonElement parameters) => parameters.ValueKind == JsonValueKind.Object && parameters.EnumerateObject().Count() == 0;

    private static bool OnlyProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = allowed.ToHashSet(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Remove(property.Name)) && names.Count == 0;
    }

    private void RemoveSessionLocked(string session)
    {
        _sessions.Remove(session);
        _sessionParents.Remove(session);
        foreach (var key in _requests.Keys.Where(key => key.SessionId == session).ToArray()) _requests.Remove(key);
    }

    private void RemoveFrameLocked(string frameId)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { frameId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var childFrame in _frameParents.Where(pair => removed.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            {
                if (removed.Add(childFrame)) changed = true;
            }
            foreach (var child in _sessionParents.Where(pair => removed.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            {
                if (_sessions.Remove(child)) changed = true;
                _sessionParents.Remove(child);
                foreach (var key in _requests.Keys.Where(key => key.SessionId == child).ToArray()) _requests.Remove(key);
            }
        }
        _relatedFrames.RemoveWhere(removed.Contains);
        foreach (var frame in removed) _frameParents.Remove(frame);
    }

    private void InvalidateLocked()
    {
        _generation++;
        _requests.Clear();
        _sessions.RemoveWhere(s => s.Length > 0);
        _sessionParents.Clear();
    }
}

