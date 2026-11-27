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
    public void StartsFailClosedAndRequiresKnownFrameAndLoaderForNetworkRequests()
    {
        var policy = new CdpCommandPolicy();
        Assert.False(policy.IsPageAllowed);
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }));

        policy.SetTopFrame("main-frame", GamePage,
            new[] { ("main-frame", (string?)null, (string?)"loader-1") });
        Assert.True(policy.IsPageAllowed);
        Assert.False(policy.TryRegisterRequest(null, "unknown-frame", "not-a-frame", "loader-1", policy.Generation, out _));
        Assert.False(policy.TryRegisterRequest(null, "old-loader", "main-frame", "old-loader", policy.Generation, out _));
        Assert.True(policy.TryRegisterRequest(null, "valid", "main-frame", "loader-1", policy.Generation, out var generation));
        Assert.True(policy.IsCurrent(null, "valid", generation));
    }

    [Fact]
    public void NewTopFrameNavigationInvalidatesPriorFrameAndSessions()
    {
        var policy = CreateAllowedPolicy();
        Assert.True(RegisterRootRequest(policy, "old", out var oldGeneration));
        Observe(policy, "Page.frameNavigated", new
        {
            frame = new { id = "new-main", url = KancolleCapturePolicy.GamePageUrl, loaderId = "new-loader", parentId = (string?)null }
        });
        Assert.True(policy.IsPageAllowed);
        Assert.False(policy.IsCurrent(null, "old", oldGeneration));
        Assert.False(policy.TryRegisterRequest(null, "old-frame", "main-frame", "old-loader", policy.Generation, out _));
        Assert.True(policy.TryRegisterRequest(null, "new-frame", "new-main", "new-loader", policy.Generation, out _));
    }

    [Fact]
    public void ResponseBodyRequiresAValidatedCurrentRequestAndSession()
    {
        var policy = CreateAllowedPolicy();
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.True(RegisterRootRequest(policy, "r1", out var generation));
        Assert.True(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1", extra = true }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r2" }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }, "unknown-session"));
        policy.CompleteRequest(null, "r1");
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "r1" }));
        Assert.Equal(generation, policy.Generation);
    }

    [Theory]
    [InlineData("https://accounts.google.com/signin/v2/identifier")]
    [InlineData("https://accounts.dmm.com/service/login/password")]
    [InlineData("https://example.invalid/")]
    public void AuthenticationAndOtherNonGameNavigationsDisableCaptureUntilCanonicalReturn(string url)
    {
        var policy = CreateAllowedPolicy();
        Assert.True(RegisterRootRequest(policy, "old", out var oldGeneration));
        Observe(policy, "Page.frameNavigated", new
        {
            frame = new { id = "main-frame", url }
        });
        Assert.False(policy.IsPageAllowed);
        Assert.False(policy.IsCurrent(null, "old", oldGeneration));
        Assert.False(policy.TryRegisterRequest(null, "blocked", "main-frame", "loader-1", policy.Generation, out _));
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }));
        Assert.False(Validate(policy, "Network.getResponseBody", new { requestId = "old" }));
        var staleCallbackCalled = false;
        Assert.False(policy.TryExecuteIfCurrent(null, "old", oldGeneration, () => staleCallbackCalled = true));
        Assert.False(staleCallbackCalled);

        Observe(policy, "Page.navigatedWithinDocument", new
        {
            frameId = "main-frame", url = KancolleCapturePolicy.GamePageUrl
        });
        Assert.True(policy.IsPageAllowed);
        Assert.True(policy.Generation > oldGeneration);
        Assert.True(policy.TryRegisterRequest(null, "new", "main-frame", "loader-1", policy.Generation, out _));
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
        Assert.True(policy.BindSessionFrame("child-1", "known-frame", null));
        Assert.True(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "child-1"));
        Assert.False(Validate(policy, "Target.setAutoAttach", new
        {
            autoAttach = false, waitForDebuggerOnStart = false, flatten = true
        }, "child-1"));
        Assert.True(policy.TryRegisterRequest("child-1", "r1", "known-frame", "child-loader", policy.Generation, out var generation));
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
        Assert.True(policy.BindSessionFrame("child-2", "known-frame", null));
        Observe(policy, "Page.frameDetached", new { frameId = "known-frame", reason = "remove" });
        Assert.False(policy.IsSessionActive("child-2"));

        // Some WebView2 CDP versions omit the optional TargetInfo.parentFrameId.
        // Such a session remains provisional until its Page.getFrameTree root is
        // bound to a frame already present in the approved top-frame tree.
        Observe(policy, "Page.frameAttached", new { frameId = "webview2-frame", parentFrameId = "main-frame" });
        Observe(policy, "Target.attachedToTarget", new
        {
            sessionId = "child-without-parent", targetInfo = new { type = "iframe", targetId = "webview2-frame" }
        });
        Assert.True(policy.IsSessionActive("child-without-parent"));
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "child-without-parent"));
        Assert.True(policy.BindSessionFrame("child-without-parent", "webview2-frame", null));
        Assert.True(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "child-without-parent"));

        Observe(policy, "Target.attachedToTarget", new
        {
            sessionId = "unrelated-child", targetInfo = new { type = "iframe", targetId = "not-in-tree" }
        });
        Assert.True(policy.IsSessionActive("unrelated-child"));
        Assert.False(policy.BindSessionFrame("unrelated-child", "not-in-tree", null));
        policy.RejectSession("unrelated-child");
        Assert.False(policy.IsSessionActive("unrelated-child"));
    }

    [Fact]
    public void DirectGameOopifNeedsExplicitApprovalAndMatchingTargetFrameId()
    {
        var policy = CreateAllowedPolicy();
        var attached = new
        {
            sessionId = "game-oopif-session",
            targetInfo = new
            {
                type = "iframe", targetId = "game-oopif-frame", parentId = "validated-page-target",
                url = "https://w07l.kancolle-server.com/kcs2/index.php"
            }
        };

        Observe(policy, "Target.attachedToTarget", attached);
        Assert.True(policy.IsSessionActive("game-oopif-session"));
        Assert.False(policy.BindApprovedGameOopifFrame("game-oopif-session", "game-oopif-frame", null));
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "game-oopif-session"));
        policy.RejectSession("game-oopif-session");
        Observe(policy, "Target.attachedToTarget", attached, approvedDirectGameOopif: true);
        Assert.True(policy.IsSessionActive("game-oopif-session"));
        Assert.False(policy.BindApprovedGameOopifFrame("game-oopif-session", "different-frame", null));
        Assert.False(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "game-oopif-session"));
        Assert.True(policy.BindApprovedGameOopifFrame("game-oopif-session", "game-oopif-frame", "game-loader"));
        Assert.True(Validate(policy, "Network.enable", new
        {
            maxTotalBufferSize = CdpCommandPolicy.MaximumBufferedBytes,
            maxResourceBufferSize = CdpCommandPolicy.MaximumBufferedBytes
        }, "game-oopif-session"));
    }

    private static CdpCommandPolicy CreateAllowedPolicy()
    {
        var policy = new CdpCommandPolicy();
        policy.SetTopFrame("main-frame", GamePage, new[] { ("main-frame", (string?)null, (string?)"loader-1") });
        return policy;
    }

    private static bool RegisterRootRequest(CdpCommandPolicy policy, string requestId, out long generation) =>
        policy.TryRegisterRequest(null, requestId, "main-frame", "loader-1", policy.Generation, out generation);

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

    private static void Observe(CdpCommandPolicy policy, string method, object parameters, string? sessionId = null,
        bool approvedDirectGameOopif = false)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            method,
            @params = parameters,
            sessionId
        }));
        policy.ObserveEvent(document.RootElement, approvedDirectGameOopif);
    }
}
