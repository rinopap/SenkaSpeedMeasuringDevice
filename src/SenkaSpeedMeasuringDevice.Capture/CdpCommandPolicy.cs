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
    private const int MaximumFrames = 256;
    private readonly object _gate = new();
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal) { string.Empty };
    private readonly HashSet<string> _relatedFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _frameLoaders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionParents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _approvedGameOopifTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SessionId, string RequestId), PolicyRequest> _requests = new();
    private long _generation = 1;
    private string? _topFrameId;
    private bool _pageAllowed;

    public long Generation { get { lock (_gate) return _generation; } }
    public bool IsPageAllowed { get { lock (_gate) return _pageAllowed; } }
    public bool IsSessionActive(string? sessionId)
    {
        lock (_gate) return _sessions.Contains(sessionId ?? string.Empty);
    }

    public bool BindSessionFrame(string sessionId, string frameId, string? loaderId)
    {
        lock (_gate)
        {
            if (!_pageAllowed || !_sessions.Contains(sessionId) || !_sessionParents.TryGetValue(sessionId, out var parentFrame) ||
                !_relatedFrames.Contains(frameId) || !IsDescendantOrSelfLocked(frameId, parentFrame)) return false;
            _sessionFrames[sessionId] = frameId;
            if (!string.IsNullOrWhiteSpace(loaderId)) _frameLoaders[frameId] = loaderId;
            return true;
        }
    }

    public bool BindApprovedGameOopifFrame(string sessionId, string frameId, string? loaderId)
    {
        lock (_gate)
        {
            if (!_pageAllowed || !_sessions.Contains(sessionId) ||
                !_approvedGameOopifTargets.TryGetValue(sessionId, out var expectedTargetFrame) ||
                expectedTargetFrame != frameId || _relatedFrames.Contains(frameId) ||
                !_sessionParents.TryGetValue(sessionId, out var parentFrame) || parentFrame != _topFrameId ||
                !_relatedFrames.Contains(parentFrame) || _relatedFrames.Count >= MaximumFrames) return false;

            // The selected game's OOPIF is absent from the parent Page.getFrameTree.
            // Admit only the child frame proven by its attached target identity and
            // selected-game URL, anchoring it to the already validated top frame.
            _relatedFrames.Add(frameId);
            _frameParents[frameId] = parentFrame;
            if (!string.IsNullOrWhiteSpace(loaderId)) _frameLoaders[frameId] = loaderId;
            _sessionFrames[sessionId] = frameId;
            _approvedGameOopifTargets.Remove(sessionId);
            return true;
        }
    }

    public void RejectSession(string sessionId)
    {
        lock (_gate) RemoveSessionLocked(sessionId);
    }

    public void SetTopFrame(string frameId, Uri url,
        IEnumerable<(string FrameId, string? ParentFrameId, string? LoaderId)>? relatedFrames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameId);
        lock (_gate)
        {
            _topFrameId = frameId;
            _pageAllowed = KancolleCapturePolicy.IsAllowedGamePage(url);
            _relatedFrames.Clear();
            _frameParents.Clear();
            _frameLoaders.Clear();
            _relatedFrames.Add(frameId);
            if (relatedFrames is not null)
                foreach (var relatedFrame in relatedFrames.Where(frame => !string.IsNullOrWhiteSpace(frame.FrameId)))
                {
                    if (_relatedFrames.Count >= MaximumFrames) { _pageAllowed = false; break; }
                    _relatedFrames.Add(relatedFrame.FrameId);
                    if (!string.IsNullOrWhiteSpace(relatedFrame.ParentFrameId))
                        _frameParents[relatedFrame.FrameId] = relatedFrame.ParentFrameId!;
                    if (!string.IsNullOrWhiteSpace(relatedFrame.LoaderId))
                        _frameLoaders[relatedFrame.FrameId] = relatedFrame.LoaderId!;
                }
            if (!_pageAllowed) InvalidateLocked();
        }
    }

    public void ObserveEvent(JsonElement message, bool approvedDirectGameOopif = false)
    {
        if (!message.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method ||
            !message.TryGetProperty("params", out var parameters)) return;

        var outerSession = message.TryGetProperty("sessionId", out var outer) ? outer.GetString() ?? string.Empty : string.Empty;
        lock (_gate)
        {
            if (method == "Target.attachedToTarget" && _pageAllowed && _sessions.Contains(outerSession) &&
                parameters.TryGetProperty("sessionId", out var child) && child.GetString() is { Length: > 0 } childId &&
                parameters.TryGetProperty("targetInfo", out var info) &&
                info.TryGetProperty("type", out var type) && type.GetString() == "iframe")
            {
                // TargetInfo.parentFrameId is optional in CDP. WebView2 OOPIF roots may
                // also be absent from the parent Page.getFrameTree. The special OOPIF
                // path is supplied only by CdpConnection after validating the attached
                // target's selected-game URL and parent target identity.
                var parentFrameId = info.TryGetProperty("parentFrameId", out var parentFrame) &&
                                    parentFrame.ValueKind == JsonValueKind.String
                    ? parentFrame.GetString()
                    : null;
                if (approvedDirectGameOopif && outerSession.Length == 0 &&
                    info.TryGetProperty("targetId", out var targetIdElement) &&
                    targetIdElement.GetString() is { Length: > 0 } targetId)
                {
                    parentFrameId = _topFrameId;
                    if (!string.IsNullOrWhiteSpace(parentFrameId) && _relatedFrames.Contains(parentFrameId) &&
                        _sessions.Count < MaximumAttachedFrames + 1)
                    {
                        _sessions.Add(childId);
                        _sessionParents[childId] = parentFrameId;
                        _approvedGameOopifTargets[childId] = targetId;
                    }
                    return;
                }
                if (string.IsNullOrWhiteSpace(parentFrameId))
                {
                    parentFrameId = _sessionFrames.TryGetValue(outerSession, out var boundFrame)
                        ? boundFrame
                        : _topFrameId;
                }

                if (!string.IsNullOrWhiteSpace(parentFrameId) && _relatedFrames.Contains(parentFrameId) &&
                    _sessions.Count < MaximumAttachedFrames + 1)
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
                if (_relatedFrames.Count >= MaximumFrames) { _pageAllowed = false; InvalidateLocked(); return; }
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

            if (method == "Page.frameNavigated" && parameters.TryGetProperty("frame", out var childFrame) &&
                childFrame.TryGetProperty("id", out var childFrameElement) && childFrameElement.GetString() is { } childFrameId &&
                childFrameId != _topFrameId && _relatedFrames.Contains(childFrameId))
            {
                var invalidFrames = DescendantsLocked(childFrameId);
                foreach (var key in _requests.Where(pair => pair.Value.FrameId is { } id && invalidFrames.Contains(id))
                             .Select(pair => pair.Key).ToArray()) _requests.Remove(key);
                if (childFrame.TryGetProperty("loaderId", out var childLoader) && childLoader.ValueKind == JsonValueKind.String)
                    _frameLoaders[childFrameId] = childLoader.GetString()!;
                else _frameLoaders.Remove(childFrameId);
                return;
            }

            if (outerSession.Length == 0 && TryGetTopNavigation(method, parameters, out var navigatedFrameId, out var url, out var loaderId))
            {
                _generation++;
                _topFrameId = navigatedFrameId;
                _pageAllowed = KancolleCapturePolicy.IsAllowedGamePage(url);
                _requests.Clear();
                // A top-document navigation starts a fresh frame tree. Never carry child
                // sessions or loader/request permits into the new document.
                _sessions.RemoveWhere(s => s.Length > 0);
                _sessionParents.Clear();
                _sessionFrames.Clear();
                _approvedGameOopifTargets.Clear();
                _relatedFrames.Clear();
                _frameParents.Clear();
                _frameLoaders.Clear();
                if (!string.IsNullOrWhiteSpace(navigatedFrameId)) _relatedFrames.Add(navigatedFrameId);
                if (!string.IsNullOrWhiteSpace(loaderId)) _frameLoaders[navigatedFrameId] = loaderId;
            }
        }
    }

    private bool TryGetTopNavigation(string method, JsonElement parameters, out string frameId, out Uri url, out string? loaderId)
    {
        frameId = string.Empty;
        url = new Uri("about:blank");
        loaderId = null;
        if (method == "Page.frameNavigated" && parameters.TryGetProperty("frame", out var frame) &&
            frame.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } parsedId &&
            (!frame.TryGetProperty("parentId", out var parent) || parent.ValueKind == JsonValueKind.Null) &&
            frame.TryGetProperty("url", out var frameUrl))
        {
            frameId = parsedId;
            if (frame.TryGetProperty("loaderId", out var loader) && loader.ValueKind == JsonValueKind.String) loaderId = loader.GetString();
            if (Uri.TryCreate(frameUrl.GetString(), UriKind.Absolute, out var parsed)) { url = parsed; return true; }
            return false;
        }
        if (method == "Page.navigatedWithinDocument" && parameters.TryGetProperty("frameId", out var sameFrameId) &&
            sameFrameId.GetString() == _topFrameId && parameters.TryGetProperty("url", out var sameDocumentUrl))
        {
            frameId = sameFrameId.GetString()!;
            if (Uri.TryCreate(sameDocumentUrl.GetString(), UriKind.Absolute, out var parsed)) { url = parsed; return true; }
            return false;
        }
        return false;
    }

    public bool TryRegisterRequest(string? sessionId, string requestId, string? frameId, string? loaderId,
        long observedGeneration, out long generation)
    {
        generation = 0;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(frameId) || loaderId is null) return false;
        lock (_gate)
        {
            var session = sessionId ?? string.Empty;
            if (!_pageAllowed || _generation != observedGeneration || !_sessions.Contains(session) ||
                !_relatedFrames.Contains(frameId) ||
                (session.Length > 0 && (!_sessionFrames.TryGetValue(session, out var sessionFrame) ||
                    !IsDescendantOrSelfLocked(frameId, sessionFrame))) ||
                (!string.IsNullOrEmpty(loaderId) && _frameLoaders.TryGetValue(frameId, out var currentLoader) && currentLoader != loaderId)) return false;
            generation = _generation;
            _requests[(session, requestId)] = new PolicyRequest(generation, frameId, loaderId);
            if (_requests.Count > 256) _requests.Remove(_requests.Keys.First());
            return true;
        }
    }

    private bool IsDescendantOrSelfLocked(string frameId, string parent)
    {
        var current = frameId;
        for (var depth = 0; depth <= MaximumFrames; depth++)
        {
            if (current == parent) return true;
            if (!_frameParents.TryGetValue(current, out var parentId)) return false;
            current = parentId;
        }
        return false;
    }

    public bool IsCurrent(string? sessionId, string requestId, long generation)
    {
        lock (_gate)
            return _pageAllowed && _generation == generation &&
                   _requests.TryGetValue((sessionId ?? string.Empty, requestId), out var recorded) &&
                   recorded.Generation == generation && RequestFrameIsCurrentLocked(recorded);
    }

    public bool TryExecuteIfCurrent(string? sessionId, string requestId, long generation, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!_pageAllowed || _generation != generation ||
                !_requests.TryGetValue((sessionId ?? string.Empty, requestId), out var recorded) ||
                recorded.Generation != generation || !RequestFrameIsCurrentLocked(recorded))
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
                "Network.enable" => _pageAllowed &&
                    (session.Length == 0 || _sessionFrames.ContainsKey(session)) && IsNetworkEnable(parameters),
                "Network.disable" => IsEmpty(parameters),
                "Network.getResponseBody" => IsResponseBody(parameters, session),
                "Target.setAutoAttach" => IsFixedAutoAttach(parameters, session) &&
                    (!parameters.TryGetProperty("autoAttach", out var enabled) || enabled.ValueKind != JsonValueKind.True || _pageAllowed),
                "Page.enable" or "Page.disable" or "Page.getFrameTree" => IsEmpty(parameters),
                _ => false
            };
        }
    }

    private bool IsResponseBody(JsonElement parameters, string session)
    {
        if (!OnlyProperties(parameters, "requestId") || !parameters.TryGetProperty("requestId", out var id) ||
            id.ValueKind != JsonValueKind.String) return false;
        return _pageAllowed && _requests.TryGetValue((session, id.GetString()!), out var request) &&
               request.Generation == _generation && RequestFrameIsCurrentLocked(request);
    }

    private bool RequestFrameIsCurrentLocked(PolicyRequest request) => request.FrameId is null ||
        (_relatedFrames.Contains(request.FrameId) &&
         (string.IsNullOrEmpty(request.LoaderId) || !_frameLoaders.TryGetValue(request.FrameId, out var loader) || loader == request.LoaderId));

    private HashSet<string> DescendantsLocked(string frameId)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { frameId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var child in _frameParents.Where(pair => removed.Contains(pair.Value)).Select(pair => pair.Key))
                if (removed.Add(child)) changed = true;
        }
        return removed;
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
        _sessionFrames.Remove(session);
        _approvedGameOopifTargets.Remove(session);
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
                _sessionFrames.Remove(child);
                foreach (var key in _requests.Keys.Where(key => key.SessionId == child).ToArray()) _requests.Remove(key);
            }
        }
        _relatedFrames.RemoveWhere(removed.Contains);
        foreach (var frame in removed) _frameParents.Remove(frame);
        foreach (var key in _requests.Where(pair => pair.Value.FrameId is { } id && removed.Contains(id))
                     .Select(pair => pair.Key).ToArray()) _requests.Remove(key);
    }

    private void InvalidateLocked()
    {
        _generation++;
        _requests.Clear();
        _sessions.RemoveWhere(s => s.Length > 0);
        _sessionParents.Clear();
        _sessionFrames.Clear();
        _approvedGameOopifTargets.Clear();
    }

    private sealed record PolicyRequest(long Generation, string? FrameId, string? LoaderId);
}
