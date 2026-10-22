# Contributing

## Two gates: CI for non-COM code, the live suite for the COM paths

This project drives a real TwinCAT XAE over its COM Automation Interface. Part
of the code references proprietary Beckhoff assemblies
(`TcEventLoggerAdsProxyLib`, `TwinCAT.Measurement.AutomationInterface`, …) that
exist **only on a machine with TwinCAT XAE installed**.

Those COM-only parts are isolated behind `#if TWINCAT` in
`TwinCatComSession.Scope.cs` / `.EventLogger.cs`, driven by the MSBuild
`$(TwinCat)` switch in `PtcXaeMcp.WinForms.csproj`:

- `$(TwinCat)` **auto-detects** from the presence of the Measurement AI DLL, so
  a developer box and the live suite build the full COM code with no extra flags.
- Force it either way with `-p:TwinCat=true` / `-p:TwinCat=false`.
- With `TwinCat=false` the Beckhoff references are dropped and the COM partials
  compile to honest "not built without TwinCAT" fallbacks.

### Gate 1 — CI (`.github/workflows/ci.yml`, job `build-and-test`)

Runs on `windows-latest` on every push/PR against `master`: builds
`src/PtcXaeMcp.WinForms/ptc-xae-mcp.sln -c Release -p:TwinCat=false`, then runs
`dotnet test src/PtcXaeMcp.Tests -p:TwinCat=false --no-build -c Release`. It is a
**genuine gate**: a red result means a real compile error or unit-test failure in the
non-COM majority of the codebase. Do not merge a PR with red CI without understanding
why.

### Gate 2 — live suite, required for COM paths

CI cannot exercise the COM code (no TwinCAT on hosted runners). Before merging a
change that touches the Scope / EventLogger / Automation-Interface paths, run
the full live suite against a real, running TwinCAT XAE:

1. Stop a running `ptc-xae-mcp.exe` (it locks its own binaries).
2. Delete `bin` and `obj` in every project and rebuild from clean, with the TwinCAT
   code included (never `-p:TwinCat=false` for a live run):
   ```
   dotnet build src/PtcXaeMcp.WinForms/PtcXaeMcp.WinForms.csproj -c Release -p:TwinCat=true
   ```
3. Start the fresh binary with the test-support tools:
   `src/PtcXaeMcp.WinForms/bin/Release/net10.0-windows/ptc-xae-mcp.exe --diagnostics`
4. Run the suite:
   ```
   $env:PTC_XAE_MCP_REQUIRE_LIVE = '1'
   dotnet test src/PtcXaeMcp.IntegrationTests/PtcXaeMcp.IntegrationTests.csproj
   ```

This opens a per-run `%TEMP%` copy of `tests/Fixtures/PtcXaeMcpFixture` (a neutral,
checked-in fixture — see [src/README.md](src/README.md), which also lists the optional
stage variables) in a real XAE. Fakes cannot catch the COM threading / DTE behaviour
that matters here. State the **exact** pass/fail/skip count in the PR
(e.g. "live 53/8/1") — not "looked OK".

## Culture: `InvariantCulture` vs `CurrentCulture` (issue #157 item 6)

Every project promotes CA1304, CA1305 and CA1310 to build errors (see
`Directory.Build.props`), so every `ToString`/`AppendLine`/`StartsWith`/
`EndsWith` call that is even slightly culture-sensitive must say which
culture it means, explicitly. The choice is not cosmetic: this server talks
to TwinCAT XAE, PLC source, and MCP clients across DE/CH-locale developer
machines, and a DE/CH decimal comma or date format silently corrupts a
machine-readable value.

- **`CultureInfo.InvariantCulture`** for anything that is a machine/protocol
  value, not prose a person reads as a sentence: numbers, dates and hex/ASCII
  formatting embedded in tool results, log lines, exception messages, file
  paths, JSON, CSV, or any text another program (or a future `dotnet test`
  assertion) parses back. This is the default — almost everything in this
  codebase is one of these, because almost everything ends up in an MCP tool
  result or a log line.
- **`CultureInfo.CurrentCulture`** (or no explicit provider, which means the
  same thing) only for text that is *purely* for a human looking at the
  WinForms UI in their own locale right now and is never read back — e.g. a
  timestamp painted into a label the user glances at, with no tool result or
  test depending on its exact format. If you are unsure whether something
  counts as "purely UI", prefer `InvariantCulture`.
- `StartsWith`/`EndsWith`/`IndexOf` on a fixed literal (a marker prefix like
  `"[Error]"`, a tree-path segment, an XAE command-id substring) use
  `StringComparison.Ordinal` — the comparison is exact-byte, not "does this
  read the same in the user's language".

## Licensing of contributions: Developer Certificate of Origin

This project is licensed under the Apache License 2.0; by section 5 of that
license every contribution you submit is licensed under the same terms. To
make that explicit, every commit must carry a **Developer Certificate of
Origin** sign-off ([developercertificate.org](https://developercertificate.org/)):

```
Signed-off-by: Your Name <you@example.com>
```

`git commit -s` adds the line for you. By signing off you certify that you
wrote the change or have the right to submit it under Apache-2.0. There is no
separate contributor license agreement. Contributions must not contain code,
documentation or binaries from Beckhoff, Microsoft or other third parties
beyond what [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) already lists —
in particular no TwinCAT DLLs, Infosys texts or customer project content.

### Quick checklist

- [ ] CI `build-and-test` green (real gate — no-TwinCAT compile + unit suite).
- [ ] If the change touches COM paths: live suite green (Gate 2)
      (state the count in the PR).
- [ ] If the feature adds user-facing behaviour, it is an MCP tool. The GUI only shows
      activity, approvals and the tool console — no buttons that rebuild an MCP tool.
- [ ] New COM code is inside `#if TWINCAT` with a sane `#else` fallback
      (throw `TwinCatNotBuilt.Exception()` or return a not-built result); verify
      a `-p:TwinCat=false` build still compiles.
- [ ] Every commit is signed off (`git commit -s`).
- [ ] New files are covered by `.github/allowed-paths.txt` (CI runs `python tools/check_paths.py`).
      The repository holds the product only: no working notes, drafts, plans or editor/assistant config.
- [ ] No customer names, project numbers, machine paths or credentials in code,
      tests, fixtures or docs.
