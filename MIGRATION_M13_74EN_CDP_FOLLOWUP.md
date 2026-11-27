# M13.1 74EN CDP iframe capture follow-up

Date: 2026-11-27

## Findings

The user-provided sanitized `/json/list` output showed eight targets: one `page` at `play.games.dmm.com/game/kancolle`, a game iframe at `w07l.kancolle-server.com/kcs2/index.php`, several unrelated iframes, and a service worker. The listening port 9224 owner was `msedgewebview2`. The capture UI remained at `CDP接続中: 対象APIを待機中`, which means no matching request event had reached the request-registration stage. It does not indicate a parser or CSV failure.

The likely compatibility issue was that the capture policy accepted iframe CDP sessions only when `TargetInfo.parentFrameId` was present. In the current CDP schema this field is optional and experimental. The actual `Target.attachedToTarget` payload from the user's WebView2 runtime was not captured, so omission is a supported hypothesis, not a confirmed root cause.

## Changes

- `src/SenkaSpeedMeasuringDevice.Capture/CdpCommandPolicy.cs`: allow an iframe session to become provisional when the optional parent frame ID is absent. Anchor it to the active parent session's already-bound frame, or to the validated game top frame for the root session.
- `Network.enable` is now rejected for provisional child sessions. It is allowed only after `BindSessionFrame` confirms the child frame is in the previously enumerated, approved game-page frame tree and is a descendant of its approved parent.
- `tests/SenkaSpeedMeasuringDevice.Tests/CdpCommandPolicyTests.cs`: cover missing optional parent-frame metadata, denial of Network enable before binding, and rejection of an iframe frame ID outside the approved tree.

This keeps API body capture constrained to the selected game page/frame tree and exact selected server API host/path. No interception, request mutation, JavaScript injection, or additional game request was added.

## Verification

- Automated tests: 73 passed, 0 failed, 0 skipped.
- Standard solution build: blocked because the running `SenkaSpeedMeasuringDevice.App` process (PID 35556) had its Release DLLs open. The process was not stopped.
- Separate Release publish: succeeded to `artifacts/M13.1-iframe-cdp-fix/`.
- Actual 74EN validation of the new build: not yet performed. The synthetic test verifies the optional-field path, but does not establish that the user's WebView2 runtime omitted `parentFrameId`.

## Manual validation still required

Close the older measurement-app instance normally, then launch `artifacts/M13.1-iframe-cdp-fix/SenkaSpeedMeasuringDevice.App.exe`. Configure the same CDP port and selected game server. With 74EN running with port 9224 and the game open, trigger a normal in-game action that refreshes the port data. Expected result: the status advances from API waiting to request/response/body capture and then measurement, and the two CSV files are created beside this test executable. Since this is a separate output folder, test CSVs will not overwrite CSVs beside the previous app.

Do not share full URLs, debugger WebSocket URLs, cookies, or response bodies when reporting results.
