# Coordinated SmartPrint QA run order

The supported one-command entry point is:

```powershell
pwsh -NoProfile -Command "& '.\\scripts\\qa\\run-smartprint-platform-qa.ps1' -Platforms @('Web','Android','Windows') -Mode Pull"
```

Pull mode is an exact-commit acceptance run, not a rebuild. It refuses dirty
tracked files, uses only the latest push-triggered CI run for the checked-out
SHA, verifies every downloaded artifact's manifest and SHA-256 values, and
checks that every required CI job completed successfully. It never falls back
to an older commit or a pull-request merge-result artifact.

## Execution sequence

1. **Exact-commit CI gate.** Confirm `portal`, `api_dotnet`,
   `agent_dotnet`, `windows_desktop`, `windows_installer`, `dotnet`,
   `postgres-integration`, and `android` all passed for the same SHA.
   This captures Linux Chromium coverage, the real Windows virtual PDF queue,
   PostgreSQL migration integration, Android emulator/package lifecycle and
   the clean-host Windows installer lifecycle.
2. **Artifact integrity.** Download Web production, Web Photo Studio fixture,
   Android isolated QA APK pair/fixture and Windows production/diagnostic
   binaries. Verify each artifact manifest against the exact commit and CI run.
3. **Shared QA harness.** Syntax-check all `scripts/qa/*.mjs` modules and run
   every `scripts/qa/*.test.mjs` test file.
4. **Web source regression.** Run every `apps/portal/tests/*.test.ts` file
   with the same serialized Node test reporter used in CI; run the Photo Studio
   Chromium acceptance suite.
5. **Responsive browser acceptance.** Exercise public, customer, shop and
   platform-admin routes at desktop, laptop, tablet and narrow mobile viewport
   sizes. API requests are intercepted by synthetic fixtures.
6. **Production Portal batch acceptance.** Run the mixed-file page rejection,
   refund allocation, audit and idempotency journey against the exact production
   Portal artifact.
7. **Windows source/runtime checks.** Run .NET API and Agent suites; desktop
   frontend typecheck; Rust check/tests; isolated diagnostic WebView2 launch and
   rendered-content smoke; then run both desktop renderer and actual native
   WebView2 mixed-batch flows.
8. **Android source/device checks.** Run Android JVM unit tests, validate the
   exact-commit isolated APK pair and fixture, then install only the
   `com.quantaedge.smartprint.localqa[.*]` package and run instrumentation on
   the selected connected device. The production customer package and its
   data are never uninstalled, replaced or cleared by this local runner.
9. **Unified evidence.** Write one `summary.json` and `summary.txt` with the
   source SHA, CI run, each required CI job, every local QA step, and PASS,
   FAIL, BLOCKED or SKIPPED outcomes.

## Tests intentionally run only on disposable CI runners

- Android production-package install/reinstall/upgrade/downgrade/signature/
  uninstall lifecycle.
- Windows installer install/reinstall/upgrade/rollback/uninstall lifecycle.
- PostgreSQL historical-schema migration integration.
- Real Windows Microsoft Print to PDF and multi-queue spooler integration.

The coordinator requires these exact-commit CI jobs to be green and records
their job URLs. It does not replay destructive lifecycle steps against a
personal phone, an existing Windows installation/service, or the user's local
database.

## Separate suites that are not silently treated as passing

- `scripts/qa/run-smartprint-qa.ps1` is a real local-data E2E journey. It
  needs a running local PostgreSQL/API/Portal/Agent stack and DPAPI-protected QA
  identities. It creates persistent QA resources (plans, tenants, shops,
  stations/printers and related test records); it is intentionally not launched
  automatically by the standard Pull command.
- `.github/workflows/live-environment-qa.yml` targets a deployed HTTPS
  environment and requires configured repository variables/secrets. It remains
  a separate, explicitly dispatched environment check.
- Manually documented acceptance catalogues still require their declared
  manual/device evidence. Their presence in documentation is not reported as
  an automated PASS.
- The `publish-containers` job only runs on pushes to `main`; this PR QA
  command never pushes to or triggers `main` just to run deployment smoke.

Adding a new `scripts/qa/*.test.mjs` or `apps/portal/tests/*.test.ts` file
automatically includes it in both CI and the local coordinator's matching
source suite. Platform-specific Gradle, .NET and Rust frameworks discover their
tests from their project manifests.
