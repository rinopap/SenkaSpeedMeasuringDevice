# Qt / sidecar integration probe

This probe is for a developer workstation with the .NET 10 SDK. It starts a loopback-only HTTP origin, sends one synthetic POST through the already-running Qt sidecar, and verifies that method, path/query, request body, response body, and origin request count are preserved.

Run it only after opening the Qt app's Communication Proxy dialog and starting the proxy on the selected loopback port. It never connects to the game or to an external host. After `PASS=True`, check that the Qt app appended exactly one row with the documented fake experience values to each CSV.

```powershell
dotnet run --project Phase6QtIntegrationProbe.csproj -- --proxy-port 8877
```

