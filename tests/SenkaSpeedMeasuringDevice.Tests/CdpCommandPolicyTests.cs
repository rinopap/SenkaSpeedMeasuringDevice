using System.Text.Json;
using SenkaSpeedMeasuringDevice.Capture;
using SenkaSpeedMeasuringDevice.Core;

namespace SenkaSpeedMeasuringDevice.Tests;

public sealed class CdpCommandPolicyTests
{
    private static readonly Uri GamePage = new(KancolleCapturePolicy.GamePageUrl);

    [Fact]
    public void AllowsOnlyFixedObservationCommandsAndArguments()
    {
        var policy = CreateAllowedPolicy();
        Assert.True(Validate(policy, "Page.enable", new { }));
        Assert.True(Validate(policy, "Page.getFrameTree", new { }));
        Assert.True(Validate(policy, "Page.disable", new { }));
        Assert.True(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }));
        Assert.True(Validate(policy, "Target.setAutoAttach", AutoAttachParams()));
        Assert.True(Validate(policy, "Target.setAutoAttach", new
        {
            autoAttach = false, waitForDebuggerOnStart = false, flatten = true
        }));
        Assert.False(Validate(policy, "Network.enable", new { maxTotalBufferSize = 0, maxResourceBufferSize = 0 }));
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxPostDataSize = 10
        }));
        Assert.False(Validate(policy, "Target.setAutoAttach", new
        {
            autoAttach = true,
            waitForDebuggerOnStart = true,
            flatten = true,
            filter = new object[]
            {
                new { type = "iframe", exclude = false }, new { exclude = true }
            }
        }));
        Assert.False(Validate(policy, "Fetch.enable", new { }));
        Assert.False(Validate(policy, "Runtime.evaluate", new { expression = "1" }));
        Assert.False(Validate(policy, "Network.replayXHR", new { requestId = "1" }));
        Assert.False(Validate(policy, "Network.setExtraHTTPHeaders", new { headers = new { } }));
    }

    [Fact]
    public void ResponseBodyRequiresAValidatedCurrentRequestAndSession()
    {
        var policy = CreateAllowedPolicy();
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.True(policy.TryRegisterRequest(null, "r1", policy.Generation, out var generation));
        Assert.True(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1", extra = true }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r2" }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }, "unknown-session"));
        policy.CompleteRequest(null, "r1");
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.Equal(generation, policy.Generation);
    }

    [Fact]
    public void NavigationInvalidatesOldRequestsAndOnlyCanonicalReturnResumesCapture()
    {
        var policy = CreateAllowedPolicy();
        Assert.True(policy.TryRegisterRequest(null, "old", policy.Generation, out var oldGeneration));
        Observe(policy, "Page.frameNavigated", new
        {
            frame = new { id = "main-frame", url = "https://example.invalid/" }
        });
        Assert.False(policy.IsPageAllowed);
        Assert.False(policy.IsCurrent(null, "old", oldGeneration));
        Assert.False(policy.TryRegisterRequest(null, "blocked", policy.Generation, out _));
        var staleCallbackCalled = false;
        Assert.False(policy.TryExecuteIfCurrent(null, "old", oldGeneration, () => staleCallbackCalled = true));
        Assert.False(staleCallbackCalled);

        Observe(policy, "Page.navigatedWithinDocument", new
        {
            frameId = "main-frame", url = KancolleCapturePolicy.GamePageUrl
        });
        Assert.True(policy.IsPageAllowed);
        Assert.True(policy.Generation > oldGeneration);
        Assert.True(policy.TryRegisterRequest(null, "new", policy.Generation, out _));
        var currentCallbackCalled = false;
        Assert.True(policy.TryExecuteIfCurrent(null, "new", policy.Generation, () => currentCallbackCalled = true));
        Assert.True(currentCallbackCalled);
    }

    [Fact]
    public void ChildSessionMustBeAttachedToAnActiveIframeAndDetachClearsItsRequests()
    {
        var policy = CreateAllowedPolicy();
        Observe(policy, "Target.attachedToTarget", new
        {
            sessionId = "child-1", targetInfo = new { type = "iframe", parentFrameId = "unknown-frame" }
        });
        Assert.False(policy.IsSessionActive("child-1"));
        Observe(policy, "Page.frameAttached", new { frameId = "known-frame", parentFrameId = "main-frame" });
        Observe(policy, "Target.attachedToTarget", new
        {
            sessionId = "child-1", targetInfo = new { type = "iframe", parentFrameId = "known-frame" }
        });
        Assert.True(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "child-1"));
        Assert.False(Validate(policy, "Target.setAutoAttach", new
        {
            autoAttach = false, waitForDebuggerOnStart = false, flatten = true
        }, "child-1"));
        Assert.True(policy.TryRegisterRequest("child-1", "r1", policy.Generation, out var generation));
        Observe(policy, "Target.detachedFromTarget", new { sessionId = "child-1" });
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "child-1"));
        Assert.False(policy.IsCurrent("child-1", "r1", generation));
        Observe(policy, "Target.attachedToTarget", new
        {
            sessionId = "child-2", targetInfo = new { type = "iframe", parentFrameId = "known-frame" }
        });
        Assert.True(policy.IsSessionActive("child-2"));
        Observe(policy, "Page.frameDetached", new { frameId = "known-frame", reason = "remove" });
        Assert.False(policy.IsSessionActive("child-2"));
    }

    private static CdpCommandPolicy CreateAllowedPolicy()
    {
        var policy = new CdpCommandPolicy();
        policy.SetTopFrame("main-frame", GamePage);
        return policy;
    }

    private static bool Validate(CdpCommandPolicy policy, string method, object parameters, string? sessionId = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(parameters));
        return policy.ValidateCommand(method, document.RootElement, sessionId);
    }

    private static object AutoAttachParams() => new
    {
        autoAttach = true,
        waitForDebuggerOnStart = false,
        flatten = true,
        filter = new object[]
        {
            new { type = "iframe", exclude = false },
            new { exclude = true }
        }
    };

    private static void Observe(CdpCommandPolicy policy, string method, object parameters, string? sessionId = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            method,
            @params = parameters,
            sessionId
        }));
        policy.ObserveEvent(document.RootElement);
    }
}

