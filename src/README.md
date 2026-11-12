# Source layout

Five projects share `ptc-xae-mcp.sln`:

| Project | Purpose | Needs running exe | Needs XAE | Runs in CI |
|---|---|---|---|---|
| `PtcXaeMcp.WinForms` | Main WinForms app + embedded MCP server | — | — | builds |
| `PtcXaeMcp.Tests` | Pure unit tests (parsers, validators, command builders) | no | no | yes |
| `PtcXaeMcp.McpTests` | MCP protocol tests against the live HTTP server | yes | no | no |
| `PtcXaeMcp.IntegrationTests` | End-to-end tests against a running TwinCAT XAE instance | yes | yes | no |
| `PtcXaeMcp.AppiumTests` | UI-automation tests against the WinForms GUI itself (Appium + WinAppDriver) | yes | no | no |

See [docs/adr/0001-test-architecture.md](../docs/adr/0001-test-architecture.md) for the reasoning behind the test-layer split.

## Logs (observability, issue #147)

Two log sinks live under `%LOCALAPPDATA%\ptc-xae-mcp\`:

| File | Content | Rolls | Retention |
|---|---|---|---|
| `host-<yyyyMMdd>.log` | Global exception handlers (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, `Application.ThreadException`), server start/stop, port-binding failures, shutdown requests (tool vs GUI), session connect/sweep, and Warning+ from ASP.NET/Kestrel/the MCP SDK | Once per UTC day | Newest N days (`PTC_XAE_MCP_LOG_KEEP_DAYS`, default 30) |
| `activity-<yyyyMMdd-HHmmss-fff>.log` | Per-run JSON-lines trace of every MCP tool call (`McpActivityJsonLog`) | Once per test run (`StartNewRun`) | Newest N runs or M days, whichever is smaller (`PTC_XAE_MCP_LOG_KEEP_RUNS` default 50 / `PTC_XAE_MCP_LOG_KEEP_DAYS` default 30) |

Both sinks are best-effort and never throw into a caller. `GET /health` on port 5010 reports the exact current-file paths (`hostLog`, `activityLog`) alongside the usual status fields.

The activity log is additionally mirrored into the repo checkout's `reports/ptc-xae-mcp/` folder, but **only** when the environment variable `PTC_XAE_MCP_REPORTS_MIRROR=1` is set — unset (the default), no file is ever written into a git checkout.

A second `ptc-xae-mcp.exe` started headlessly (`--http-server`, or with redirected stdin — the way Claude Code launches it) while another instance already holds the single-instance mutex logs a warning to the host log and exits with code 2, without showing a `MessageBox`. An interactive double-click start still shows the "already running" dialog.

## Running tests

**Unit (always runs):**

```powershell
dotnet test src\PtcXaeMcp.Tests
```

**MCP protocol (requires the exe running on port 5010):**

```powershell
# Start the exe in another terminal first
.\src\PtcXaeMcp.WinForms\bin\Debug\net10.0-windows\ptc-xae-mcp.exe

# Then:
dotnet test src\PtcXaeMcp.McpTests
```

Tests skip gracefully via `SkipException` if port 5010 isn't reachable.

**Integration (requires TwinCAT XAE 3.1.4026 on the machine):**

The suite runs against the neutral fixture `tests/Fixtures/PtcXaeMcpFixture` (one PLC with MAIN,
a step-chain FB, a GVL, STRUCT/ENUM/ALIAS DUTs, an interface, a PLC visualization with its manager, a disabled (adapter-free) EtherCAT master + EK1100
and an NC task; Beckhoff standard libraries only). It never opens the committed copy: with
`PTC_XAE_MCP_REQUIRE_LIVE=1` it copies the fixture to `%TEMP%\ptc-xae-mcp-fixture-runs\run-…`,
starts the host if port 5010 is dead (newest `src\PtcXaeMcp.WinForms\bin\**\ptc-xae-mcp.exe`, or
`PTC_XAE_MCP_HOST_EXE`) and opens the copy. Paths and symbols live in
`src\PtcXaeMcp.IntegrationTests\FixtureLayout.cs`.

```powershell
dotnet build src\PtcXaeMcp.WinForms\ptc-xae-mcp.sln -c Release -p:TwinCat=true
$env:PTC_XAE_MCP_REQUIRE_LIVE = '1'
dotnet test src\PtcXaeMcp.IntegrationTests --filter Stage=XAE
```

Without `PTC_XAE_MCP_REQUIRE_LIVE` (and without `PTC_XAE_MCP_FIXTURE_SLN`) every live test skips.
`PTC_XAE_MCP_FIXTURE_SLN` points the suite at another, disposable `.sln` (used as-is). Recreate
the fixture with `python tests\Fixtures\create_fixture.py --force` (needs `pywin32` + `mcp`).

**Appium (requires Appium server + WinAppDriver/appium-windows-driver, and the exe running):**

```powershell
# Start the exe, then an Appium server, then:
dotnet test src\PtcXaeMcp.AppiumTests
```

Drives the WinForms GUI itself (tab navigation, controls) rather than the MCP protocol — see
`PtcXaeMcp.AppiumTests/AppiumFixture.cs` for the driver/session setup it expects. Not part of
CI (no display/driver on the hosted runner); run it locally before a GUI-affecting change.
