# M13.3 74EN OOPIF frame-link follow-up

Date: 2026-11-27

## Confirmed cause

The M13.2 tooltip reported that the game iframe did not match the approved frame tree. A sanitized, read-only comparison of local CDP metadata confirmed:

- The selected game page target is `play.games.dmm.com/game/kancolle`.
- The game runs in a separate iframe target at the selected Linga host and `/kcs2/index.php`.
- The iframe target's root frame ID equals its target ID, but that root frame is absent from the parent page's `Page.getFrameTree` result. The prior policy therefore rejected it when requiring every child root frame to be in that tree.
- `Target.getTargetInfo` identifies the iframe target as a direct child of the selected page target. The protocol exposes `parentId`; the frame-level `parentFrameId` is optional.

This explains the exact M13.2 status. It is based on the user's live WebView2 metadata, not merely a synthetic assumption. No API body, request parameters, cookies, or credentials were read for this comparison.

## M13.3 changes

- Added a narrow OOPIF path for the directly attached game iframe only.
- It is eligible only when the CDP event says its parent target is the already validated game page, its URL is HTTPS on the selected server host at `/kcs2/index.php`, and its own frame-tree root ID matches the target ID.
- The child frame is anchored to the validated page frame only after those checks. `Network.enable` remains forbidden before the binding succeeds.
- Other iframe targets still require normal membership in the approved page frame tree. OOPIF status does not enable capture of unrelated frames.
- Added tests for explicit direct-OOPIF approval, target/frame ID matching, denial before binding, and successful API capture from the synthetic OOPIF.

No request interception, API changes, JavaScript injection, additional game requests, or response-body capture outside the exact API allowlist was added.

## Verification

- Automated tests: 74 passed, 0 failed, 0 skipped.
- Release publish: succeeded to `artifacts/M13.3-oopif-frame-link/`.
- Actual 74EN validation with M13.3: passed, confirmed by the user.
  - All experience-per-hour measurements updated.
  - Both CSV files were generated and new log rows were appended.
  - Normal application shutdown passed.
  - Forced application termination passed.
  - Relaunch after both shutdown paths passed.
- The M13.2 frame-binding failure is resolved for the tested 74EN 5.4.3EN/WebView2 setup. Other WebView2 runtime and 74EN versions have not been tested.

## Manual validation

The procedure above was completed successfully by the user. The verified M13.3 test CSVs are beside its executable.

When reporting, share only the status tooltip text and whether both CSV files appeared. Do not send full URLs, debugger WebSocket URLs, cookies, request parameters, or response bodies.
