# M13.2 74EN CDP diagnostics follow-up

Date: 2026-11-27

## Current evidence

- The user confirmed the measurement application is configured for CDP, port 9224, and Linga.
- A sanitized local CDP target listing showed the expected game page and an iframe at `w07l.kancolle-server.com/kcs2/index.php`; the WebView2 listener owns port 9224.
- The M13.1 build still did not create CSV files during a user-triggered experience update. The UI's short label showed only `CDP接続中`; this does not distinguish whether the game iframe session was attached.
- The actual `Target.attachedToTarget` payload and its Network events have not yet been captured. Therefore the M13.1 optional-`parentFrameId` compatibility hypothesis remains unconfirmed.

## M13.2 changes

- Added transient status detail when the selected-server game iframe is attached, rejected, or fails approved-frame-tree validation.
- Added transient status detail when the exact API path is observed with a nonmatching host/HTTPS condition or when required frame metadata is absent.
- The displayed details contain no URL, request parameters, credentials, or response body and are not persisted.
- Extended the synthetic test to omit optional `parentFrameId`, validate the selected game iframe monitoring state, and validate the redacted host-mismatch diagnostic.

The CDP policy remains observation-only: it does not enable Fetch interception, modify or send game requests, inject scripts, or read non-target response bodies. Network monitoring is enabled on a child session only after frame-tree validation.

## Verification

- Automated tests: 73 passed, 0 failed, 0 skipped.
- Release publish: succeeded to `artifacts/M13.2-cdp-diagnostics/`.
- Actual 74EN verification of M13.2: pending user-triggered test. No claim is made that the capture issue is fixed until a real API response reaches the parser and CSV writer.

## Next manual check

Close the running M13.1 measurement application normally. Launch `artifacts/M13.2-cdp-diagnostics/SenkaSpeedMeasuringDevice.App.exe` while 74EN remains open with CDP port 9224. Hover over the status label for its tooltip, then trigger one ordinary in-game action that refreshes port data. Report only the tooltip text and whether CSV files appeared beside the M13.2 executable. Do not send full URLs, debugger WebSocket URLs, cookies, request parameters, or response bodies.
