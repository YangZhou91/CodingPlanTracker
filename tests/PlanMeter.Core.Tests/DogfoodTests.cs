using System;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// D-07 manual dogfood checklist marker. This class is NOT exercised by the default
/// <c>dotnet test</c> suite — it carries <c>[Trait("Category","Dogfood")]</c> on every
/// fact so the suite filter <c>"Category!=Dogfood"</c> skips it. The plan's checkpoint
/// task is the human UAT; this file exists so the developer-run steps are discoverable
/// from the test surface (and so a future <c>-filter "Category=Dogfood"</c> lights them
/// up explicitly, should CI want to surface the manual steps).
/// </summary>
/// <remarks>
/// <para><b>Why a test class for manual steps?</b> Plan 01-02 Task 3 specifies that the
/// optional one-shot developer curl (RESEARCH §C4 — pins the Z.ai field names verbatim
/// into <c>both-windows.json</c>) is documented as a checklist item, NOT a CI test.
/// D-07 forbids live-endpoint tests in the default suite.</para>
/// <para><b>How to run the dogfood:</b> See
/// <c>.planning/phases/01-z-ai-vertical-slice-safe-foundations-reference-adapter/01-02-PLAN.md</c>
/// checkpoint task (SC#1..SC#5 + drag + clear-confirm + optional curl).</para>
/// </remarks>
public sealed class DogfoodTests
{
    /// <summary>
    /// SC#1..SC#5 + drag + clear-confirm. Marked Dogfood so the default suite skips it;
    /// the developer runs the steps manually per the plan's checkpoint task.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void SC1_to_SC5_manual_dogfood_checklist()
    {
        // 1. SC#1 (WIDGET-01 focus safety): with the widget visible, type in Notepad —
        //    no keystroke is lost; launching or refreshing PlanMeter mid-typing does
        //    not interrupt. (Run: `dotnet run --project src/PlanMeter.App`.)
        //
        // 2. SC#2 (ZAI-01 + DATA-01/02/03): Right-click → 'Edit Z.ai key…' → paste a
        //    real Z.ai API key → click 'Save key'. After the D-04 test-fetch the row
        //    shows the most-binding window's used% (Accent green if Ok, amber if near
        //    limit), the '5H' or 'WEEK' chip, and a relative 'now'/'3m' timestamp.
        //    Hover the chip — the tooltip lists BOTH windows line-per-window.
        //
        // 3. SC#3 (SEC-03/04): Open %LOCALAPPDATA%\PlanMeter\logs\planmeter-*.log and
        //    grep -E 'eyJ|sk-|Bearer ' — ZERO matches. Confirm
        //    %LOCALAPPDATA%\PlanMeter\credentials\zai.key.bin exists and is opaque
        //    DPAPI bytes (Format-Hex), not the literal key.
        //
        // 4. SC#4 (SEC-02 allow-list): Temporarily change the named-"zai" client's
        //    host from api.z.ai to evil.example (in HttpExtensions.cs). Refresh — the
        //    request is refused BEFORE it leaves the machine (InvalidOperationException
        //    in the log; row goes to ERROR). Revert the change.
        //
        // 5. SC#5 (SEC-04 telemetry-off): With the app running, in Resource Monitor
        //    (Network → PlanMeter.exe) or `netstat -ano | findstr <pid>` the ONLY
        //    outbound connection is to api.z.ai. No telemetry/crash-upload/update-check.
        //
        // 6. Manual Refresh + 10-min auto-refresh (D-02/D-01): Right-click → Refresh
        //    now. The row re-fetches within ~1–2s. Leave running 10 min → row refreshes
        //    automatically. Rapidly double-click Refresh now → confirm via the log that
        //    only ONE fetch is dispatched (R3 funneling).
        //
        // 7. Clear key (destructive confirm): Right-click → Clear saved key → the inline
        //    'Clear saved key? [Clear] [Cancel]' confirm appears (non-modal). Click
        //    Clear → blob deleted; row returns to NO KEY. Confirm zai.key.bin is gone.
        //
        // 8. Drag (WIDGET-02): Drag from the provider-name + figure area — the window
        //    moves. Try dragging from the chip / timestamp / key-entry / refresh glyph
        //    — it must NOT drag.
        //
        // Type "approved" (or describe any failure) at the plan checkpoint.
        Assert.True(true, "manual dogfood checklist — see method body for SC#1..SC#5 + drag + clear-confirm");
    }

    /// <summary>
    /// Phase-3 SC#1 — settings-window key enter/rotate/clear (Plan 03-02 checkpoint task).
    /// Manual dogfood steps; see the 03-02-PLAN.md checkpoint for the exact verification.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void SC1_settings_window_key_enter_rotate_clear_checklist()
    {
        // 1. Run `dotnet run --project src/PlanMeter.App`.
        //
        // 2. Right-click the widget → `Settings…`. The window opens (native title bar,
        //    `PlanMeter Settings`, ~360 wide). Right-click `Settings…` again — the SAME
        //    window activates, no second instance.
        //
        // 3. The Z.ai card shows `No key`. `Edit key…` opens a BLANK masked field (never
        //    pre-filled, never echoed). Click `Save key` with the field empty — nothing
        //    happens (Save is disabled until non-empty).
        //
        // 4. Paste a real Z.ai API key → `Save key`. After the test-fetch the status
        //    flips to `Saved ✓`, the edit block collapses, and the widget strip's Z.ai row
        //    refreshes immediately (the save-path bypass — not gated by the 60s window).
        //
        // 5. `Edit key…` again — the field is BLANK (rotation = blank re-entry; the
        //    existing value is never shown). Cancel returns to `Saved ✓`.
        //
        // 6. `Clear saved key` → the inline `Clear saved key? [Clear] [Cancel]` confirm
        //    appears. `Clear` → status `No key` and the strip row shows the NO KEY state.
        //    Confirm %LOCALAPPDATA%\PlanMeter\credentials\zai.key.bin no longer exists.
        //
        // 7. Re-save a real key, then run the SEC-03 log check from the SC#3 step above
        //    (the token-shape grep over the log dir) — ZERO matches. Confirm config.json
        //    under %LOCALAPPDATA%\PlanMeter (if present) contains no key-shaped value.
        //
        // 8. In the window, type into the field — the widget strip must NOT lose
        //    focus-safety behavior for other apps (the window is activatable by design,
        //    D-02; verify typing in Notepad still works after the window is closed).
        //
        // Type "approved" (or describe any failure) at the 03-02 checkpoint.
        Assert.True(true, "Phase-3 SC#1 manual dogfood checklist — see method body for the settings-window key enter/rotate/clear steps");
    }

    /// <summary>
    /// Phase-3 SC#2 — live enable/disable, persisted (Plan 03-03 checkpoint task). Manual
    /// dogfood steps; see the 03-03-PLAN.md Task 2 for the exact verification. D-04 pin:
    /// disabling a provider must NOT delete its DPAPI blob.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void SC2_enable_disable_toggle_live_and_persisted_checklist()
    {
        // 1. Run `dotnet run --project src/PlanMeter.App` (Debug → the Z.ai row; Phase-4
        //    providers join as they land).
        //
        // 2. Right-click the widget → `Settings…`. EVERY provider card shows
        //    an enable/disable toggle at the card header's right edge, `IsChecked` ON.
        //    (AutomationProperties.Name = `Enable {Provider}` — verify via the
        //    Narrator or UI Automation tree if desired.)
        //
        // 3. Toggle Z.ai OFF → the strip's Z.ai row disappears IMMEDIATELY (no restart);
        //    observe the log — no further Z.ai fetch occurs (the poller parks; R3/manual
        //    refresh signals are consumed by the park, never fetched).
        //
        // 4. Toggle Z.ai ON → the Z.ai row returns immediately and FETCHES immediately
        //    (the D-06 immediate first fetch funneled through the R3 path, not a restart).
        //
        // 5. Quit and relaunch → the enabled/disabled state is PRESERVED (D-05): Z.ai stays
        //    in whatever state you left it (verify `%LOCALAPPDATA%\PlanMeter\config.json`
        //    now carries `enabledProviders`; a disabled provider is absent from the list).
        //
        // 6. D-04 pin — while Z.ai is disabled, confirm `%LOCALAPPDATA%\PlanMeter\credentials\zai.key.bin`
        //    STILL EXISTS (disabling is a visibility/polling toggle; it never deletes the
        //    key — the settings-window `Clear saved key` remains the only delete path).
        //
        // 7. Disable EVERY provider → the strip shows `No providers enabled.` +
        //    `Enable a provider in Settings to see usage.` + a `Settings…` button. Click
        //    the button → the settings window opens (empty-stack re-entry, W8).
        //
        // 8. SEC-04 — confirm config.json still contains NO key-shaped value
        //    (`grep -E 'eyJ|sk-' %LOCALAPPDATA%\PlanMeter\config.json` → zero matches).
        //
        // Type "approved" (or describe any failure) at the 03-03 checkpoint.
        Assert.True(true, "Phase-3 SC#2 manual dogfood checklist — see method body for the enable/disable toggle steps");
    }

    /// <summary>
    /// Phase-3 SC#3 — polling interval, live-applied + persisted (Plan 03-04 checkpoint
    /// task). Manual dogfood steps; see the 03-04-PLAN.md checkpoint for the exact
    /// verification. D-13 pin: changing the interval does NOT fetch immediately — the next
    /// scheduled poll uses the new cadence.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void SC3_polling_interval_live_applied_and_persisted_checklist()
    {
        // 1. Run `dotnet run --project src/PlanMeter.App`.
        //
        // 2. Right-click the widget → `Settings…`. Below the provider cards, the `Polling`
        //    section shows the `Polling interval` label with the preset dropdown
        //    (`5 min` / `10 min` / `15 min` / `30 min` / `60 min`), the current interval
        //    pre-selected (default `10 min` on a fresh install), and the helper copy
        //    `Changes take effect on the next scheduled poll.`
        //
        // 3. Select `5 min` → NO immediate fetch fires (observe the log — the selection
        //    itself performs no fetch, D-13); the NEXT scheduled poll uses the new ~5-min
        //    cadence (log timestamp gaps). Restart the app → the selection persists
        //    (config.json pollIntervalSeconds == 300; the dropdown still shows `5 min`).
        //
        // 4. A sub-5-min value is NOT present in the dropdown (only the five presets, D-11).
        //
        // 5. CROSS-PLAN CONTRACT: select `10 min`, then toggle Z.ai OFF and back ON, then
        //    restart → BOTH the `10 min` interval AND the toggle state survive (a toggle's
        //    persist call must carry the LIVE interval — it must not revert pollIntervalSeconds
        //    in config.json to a stale startup value).
        //
        // 6. W7 (optional, to exercise the revert): make %LOCALAPPDATA%\PlanMeter\config.json
        //    unwritable, select a different preset → the dropdown reverts to the current
        //    interval and `Couldn't save settings. Changes won't survive a restart.` shows;
        //    the live interval is unchanged. Restore write access.
        //
        // 7. SEC-03/04 (unchanged): `grep -E 'eyJ|sk-|Bearer ' %LOCALAPPDATA%\PlanMeter\logs\planmeter-*.log`
        //    → ZERO matches; config.json holds only the interval + enabled providers (no key).
        //
        // Type "approved" (or describe any failure) at the 03-04 checkpoint.
        Assert.True(true, "Phase-3 SC#3 manual dogfood checklist — see method body for the polling-interval steps");
    }

    /// <summary>
    /// Optional one-shot dogfood curl (RESEARCH §C4 — pins the Z.ai field names).
    /// Run from a shell with a real Z.ai key:
    /// <code>
    /// curl -s -H "Authorization: Bearer &lt;YOUR_ZAI_KEY&gt;" -H "Accept: application/json" \
    ///     "https://api.z.ai/api/monitor/usage/quota/limit" | jq .
    /// </code>
    /// If the response field names match the both-windows fixture, no action needed.
    /// If they differ, update <c>tests/PlanMeter.Core.Tests/Fixtures/zai-quota-limit-both-windows.json</c>
    /// verbatim with the captured body, then re-run
    /// <c>dotnet test --filter "FullyQualifiedName~ZaiNormalizerTests"</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void Optional_dogfood_curl_to_pin_zai_field_names()
    {
        // Documented here per Plan 01-02 Task 3 action. NOT a CI test — D-07 forbids
        // live-endpoint tests in the default suite. Developer-run only.
        Assert.True(true, "developer-run curl — see method body for the exact command");
    }

    /// <summary>
    /// BOOT-01 SC#1–#3 — Start with Windows checkbox creates/removes PlanMeter.lnk
    /// and the widget auto-launches after sign-in. Category=Dogfood so the default
    /// suite skips it; run the numbered steps manually.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void BOOT01_SC1_SC3_manual_dogfood_checklist()
    {
        // 1. `dotnet run --project src/PlanMeter.App`. Open Settings. The Startup card
        //    is unchecked (unless a leftover .lnk exists). Check "Start with Windows".
        //    Confirm %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\PlanMeter.lnk
        //    exists (Win+R → shell:startup) and Properties → Target is the running EXE.
        //
        // 2. Uncheck; confirm the .lnk is gone. Re-check to restore it.
        //
        // 3. Close and reopen Settings — the box is checked (file-derived state, SC#2).
        //
        // 4. Reboot or sign out/in with the box checked — the widget appears with no
        //    manual start (SC#3). Double-click the EXE: the running instance comes
        //    forward; no second window.
        //
        // 5. Documented limitation: if you move the EXE, uncheck and re-check to
        //    rewrite the shortcut. No auto-repair.
        Assert.True(true, "BOOT-01 SC#1-#3 manual dogfood checklist — see method body");
    }

    /// <summary>
    /// REFRESH-04 SC#4 — Codex timer-poll egress observability. Category=Dogfood so
    /// the default suite skips it; inspect the live log after an idle interval.
    /// </summary>
    [Fact]
    [Trait("Category", "Dogfood")]
    public void REFRESH04_SC4_codex_timer_egress_dogfood_checklist()
    {
        // 1. With ~/.codex/auth.json present, launch the app and leave it idle ~11 min
        //    (no manual Refresh).
        //
        // 2. Open %LOCALAPPDATA%\PlanMeter\logs\planmeter-<today>.log. Confirm recurring
        //    "poller fetch start (provider=codex" lines at roughly the interval
        //    (scheduled timer, no manual refresh) and host-only egress lines naming
        //    chatgpt.com (SC#4).
        //
        // 3. Optionally run Win+R shell:startup then reboot per the BOOT-01 checklist.
        Assert.True(true, "REFRESH-04 SC#4 Codex timer egress dogfood checklist — see method body");
    }
}
