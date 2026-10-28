# Security

Pneutronic XAE MCP (`ptc-xae-mcp`) runs a local HTTP server (Kestrel, port 5010) that exposes the
MCP protocol and a small set of tools that can read and write a TwinCAT XAE
project and control a real PLC runtime. This document describes the
security model as of the hardening in issue #156.

## Threat model

The server is designed to be reachable **only from the same machine**. It
is not intended to be exposed to a network, and there is no user-management
or multi-tenant model — anyone who can reach the port can call every tool.
The hardening below defends against the two realistic local threats: a
malicious or compromised web page in a browser on the same machine
(DNS rebinding / CSRF-style requests), and, optionally, another local
process or user account that should not have access.

It does **not** defend against an attacker who already has code execution
as the same Windows user the server runs as — that attacker can already do
everything the server can do, directly.

## Loopback-only by default

The server binds to `http://localhost:5010` only (Kestrel `app.Urls`) — it
never binds to `0.0.0.0` or a specific NIC address, so it is not reachable
from another machine on the network without an explicit port-forward
(which this project does not set up and does not recommend).

## Host and Origin checks

Every request is checked by middleware (`McpBridge.cs`, decision logic in
`Security/LocalhostGuard.cs`) before it reaches any tool or the `/health`
endpoint:

- **Host header** must be exactly `localhost:5010`, `127.0.0.1:5010`, or
  `[::1]:5010` (case-insensitive). Anything else — including a DNS name an
  attacker registered to resolve to `127.0.0.1` — is rejected with
  **403 Forbidden**. This is the standard defense against DNS rebinding:
  a page served from `evil.example` cannot make the browser send a request
  with `Host: evil.example` succeed against this server, because Kestrel
  itself does not enforce a Host allow-list — this middleware does.
- **Origin header**, when present, must be an `http://` or `https://`
  origin whose host is `localhost`, `127.0.0.1`, or `::1`. Most MCP
  clients (a stdio-bridged Claude Code process, curl, a Python script)
  never send an `Origin` header at all — that case is allowed, since it is
  not a browser request. A request that *does* carry a non-loopback
  `Origin` (e.g. a `fetch()` from a malicious page, even if it somehow got
  the Host check to pass) is rejected with **403 Forbidden**.

## Optional bearer token

Authentication is **off by default** — anyone who can reach loopback:5010
on this machine can call every tool, matching "single local user, single
local machine" as the deployment assumption.

Setting the environment variable `PTC_XAE_MCP_TOKEN` before starting the
server turns on a bearer-token check for every request except `/health`:
the request must carry `Authorization: Bearer <token>` with the exact
configured token (compared in constant time), or the server responds
**401 Unauthorized**.

`GET /health` never requires the token — it is deliberately kept reachable
as a plain liveness probe (see the code comment in `McpBridge.cs`) — but
its JSON response includes `"authRequired": true|false` so a caller can
tell whether the rest of the API needs the token before trying it.

## File-path allow-list (`PathPolicy`)

Several MCP tools accept an arbitrary file-system path as a parameter
(reading a PLC source body from a file, writing an exported library or
scope recording, importing an IODD, starting a runtime executable). Every
such parameter is checked by `Security/PathPolicy.cs`
(`Security/McpPathGuard.cs` adapts it to each tool's "[Error] ..." string
convention) **before** the coordinator or any COM/filesystem call runs:

- `write_pou_body` / `write_pou_member` (`declarationFile`,
  `implementationFile`)
- `write_gvl` (`declarationFile`)
- `plcopen_export` / `plcopen_import` (`filePath`)
- `install_library` (`libraryPath`), `save_as_library` (`outputPath`)
- `get_mapping_info` (`outputPath`), `save_scope_data` (`path`)
- `import_iodd` (`sourcePath`)

A path is allowed only when it resolves (`Path.GetFullPath`) under one of:

- the **open solution's directory** (resolved from the active TwinCAT
  session; not available for tools that do not require an open solution,
  e.g. `import_iodd`),
- `%TEMP%`,
- `%LOCALAPPDATA%\ptc-xae-mcp`,
- the local **TwinCAT installation directory** (registry
  `HKLM\SOFTWARE\WOW6432Node\Beckhoff\TwinCAT3\InstallDir`, then
  `%TwinCAT3Dir%`, then the default
  `Program Files (x86)\Beckhoff\TwinCAT\3.1` path — the same resolution
  `BeckhoffInstallLocator` uses for the Beckhoff DLL runtime-loading seam,
  see below), or
- an extra root listed in `PTC_XAE_MCP_ALLOWED_PATHS` (`;`-separated),
  for a deployment that needs a path outside all of the above.

A disallowed path is rejected with an `[Error] <param>: ...` string,
matching every other tool's existing error convention, and never reaches
the filesystem or a COM call.

`umr_start`'s `exePath` gets a narrower check
(`PathPolicy.IsAllowedRuntimeExe`): it must be an `.exe` under a local
TwinCAT installation directory specifically — it can never point to an
arbitrary executable, even one inside an otherwise-allowed root.

## Beckhoff DLLs are not redistributed by default

Unrelated to network security but part of the same hardening pass
(#156 item 4): the TcEventLogger ADS-proxy managed interop assembly and
its native companion DLL are proprietary Beckhoff components. By default
(`BundleBeckhoffDlls=false`, the new default) they are not copied into
the build output/installer — they are resolved at runtime from the local
TwinCAT installation by `Security`'s sibling,
`TwinCat/BeckhoffAssemblyResolver.cs` (an `AssemblyLoadContext.Resolving`
handler and a `NativeLibrary` DllImport resolver). See
`THIRD-PARTY-NOTICES.md` for why, and the "Needs live verification"
section below for what a live run must confirm.

## Update check (opt-in)

The app has **no self-update**: it never downloads, verifies or runs an
installer. What it can do, and only when a person asks for it, is find out
whether a newer release exists (`Updates/UpdateCheck.cs`):

- **Trigger.** *Menu → Check for updates…*, or at start when the box
  *Check for a new release at start* in *Settings…* is on. That box is
  **off by default** (`checkForUpdates` in `%APPDATA%\ptc-xae-mcp\ui.json`).
  There is deliberately **no MCP tool** for it, so an AI client (or a prompt
  injected through project content) can never trigger a request or steer a
  person towards a download.
- **The request.** One `HEAD https://github.com/Pneutronic/ptc-xae-mcp/releases/latest`
  without following the redirect; the version is read from the `Location`
  header (`…/releases/tag/v2.3.0`). No GitHub API, no token, no cookies.
  The only data sent is the `User-Agent` (`ptc-xae-mcp/<version>`) and,
  inevitably, the machine's IP address to github.com. 5 s timeout, off the
  UI thread, failure is a log line and never a dialog at start.
- **The result.** A line in the run log, a note in the header and the menu
  item renamed to *Update available: x.y.z…*, which opens the release page in
  the browser. Installing remains the manual, administrator-driven step
  described in the README (download, check the `.sha256`, run the installer)
  or `winget upgrade`.
- **Switching it off for everyone.** Administrators set the machine policy
  `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Pneutronic\ptc-xae-mcp\DisableUpdateCheck`
  (DWORD) to `1`: the box is then disabled and unchecked, the menu item is
  disabled and no request is ever sent, whatever `ui.json` says. The value is
  read once at start. For a single process (scripts, CI, kiosk accounts) the
  environment variable `PTC_XAE_MCP_UPDATE_CHECK=0` does the same. The Settings
  dialog shows which of the three is in effect.
- **What it does not protect against.** The check only tells you a version
  number. It does not authenticate the release (the installer is not
  code-signed; see the README), and a newer version is not a reason to
  interrupt a commissioning session: the app never restarts itself.

## Needs live verification (not exercised in this sandbox)

This hardening was implemented and unit-tested (`LocalhostGuardTests`,
`PathPolicyTests`) in a sandbox without a running MCP server or live
TwinCAT XAE. The maintainer must verify, against the real server:

1. **Host/Origin middleware** — start the server, then:
   - `curl -H "Host: evil.example" http://localhost:5010/health` → expect
     `403`.
   - A normal MCP client request (Claude Code, or `curl` without a custom
     Host header) still succeeds end to end (handshake + at least one
     tool call) — the middleware must not have broken the happy path.
   - A request with a non-loopback `Origin` header → expect `403`.
2. **Bearer token** — set `PTC_XAE_MCP_TOKEN`, restart the server, and
   confirm: a tool call without the header → `401`; `/health` without the
   header → `200` with `"authRequired": true`; a tool call with the
   correct `Authorization: Bearer <token>` → succeeds.
3. **PathPolicy against real tool calls** — with a live solution open,
   confirm a `write_pou_body` call with `declarationFile` pointing outside
   every allowed root is refused with the new `[Error] declarationFile: ...`
   message instead of reaching the coordinator, and that a legitimate
   in-solution or `%TEMP%` path still works exactly as before.
4. **Beckhoff DLL runtime loading** (`BeckhoffAssemblyResolver`) — build
   with the new default (`BundleBeckhoffDlls` unset/false), do **not**
   copy the DLLs manually, start the host, and exercise
   `twincat_read_event_logger`/`twincat_confirm_event_logger` against a
   running TC3 runtime. Confirm the managed interop assembly and the
   native `TcEventLoggerAdsProxy.dll` both load from the TwinCAT
   installation directory (not from the app base directory), and that the
   existing `0x8007007E` diagnostic in `DescribeEventLoggerError` still
   fires correctly if the resolver's search comes up empty (e.g. rename
   the TwinCAT `TcEventloggerV2` folder temporarily to simulate a missing
   install). Then confirm `-p:BundleBeckhoffDlls=true` still reproduces the
   old bundle-everything behavior for anyone who needs it.

## Reporting a vulnerability

Please do **not** open a public issue for a vulnerability. Use GitHub's
private vulnerability reporting ("Security" tab → "Report a vulnerability")
on this repository, or e-mail support@pneutronic.ch. Describe the affected
version (`/health` reports it), the tool or endpoint involved and how to
reproduce the problem. We will acknowledge the report, keep you informed
while we work on a fix, and credit you in the release notes unless you
prefer otherwise. Fixes ship as a new release with a note in CHANGELOG.md.
