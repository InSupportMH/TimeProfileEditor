# TimeProfileEditor

MIP plugin for Milestone XProtect Smart Client. Operators edit **existing** time profiles; they never create or delete profiles, and they never change any other XProtect setting.

Human docs: [README.md](README.md) (product), [HANDOFF.md](HANDOFF.md) (current work). This file is for coding agents.

## Never change

- `PluginIds.PluginDefinition` — identity of the security namespace. Changing it silently drops every granted role permission.
- `SecurityActionIds` (`TimeProfileEditor.View` / `TimeProfileEditor.Edit`) — renaming one revokes it everywhere.
- `UpgradeCode` in `installer/Package.wxs` — that is what makes a new MSI replace an installed one.
- MIP SDK version in `Directory.Build.props` (currently 25.1.3) unless the decision is to drop older VMS versions. Plugins load in the SDK they were built against and later, never earlier. Reach newer APIs reflectively (see `PluginSecurity.TryDetectNamespace`).
- Release number: only `<Version>` in the relevant `.csproj`. MSI, info panel, plugin list and file properties all read the assembly. Do not hard-code a second copy.

## Product bounds

- One client binary for every XProtect tier. Write path is chosen from the **server's answer**, not from a licence flag. Do not revive per-edition packages.
- `-Edition Measurement` is a lab instrument with no in-plugin permission check. Never the default, never ship it.
- Unsupported recurrence (daily, monthly, yearly, every-other-week, sunclock) is **shown and left alone**. Do not rewrite it as a weekly pattern.
- Shared client/server types are **linked source** in `TimeProfileEditor.Server.csproj`, not a third assembly. Adding a shared file means adding the `<Compile Include=... Link=...>` line. Anything that references WPF stays in the client.

## Security (two independent layers)

Layer 1 (`PluginSecurity`) decides whether the UI is offered. Layer 2 is the actual protection: every Configuration API call as the signed-in user, or — when that is refused — the Event Server component, which still gates **reads and writes** in this order: token authentic → token identity matches the claim → that identity has `TimeProfileEditor.Edit` / `View`.

Do not:

- Treat admin status or configuration-write access as a substitute for the plugin's own permissions.
- Let a source that cannot see the namespace veto one that can. One authoritative yes is enough; only a source answering for the signed-in user may say no.
- Treat an unanswered check as a no — show the tab read-only with the reason.
- Print a bearer token, a claim value, or anything that reconstructs one. Diagnostics: length, shape, expiry, claim **names** only.
- Put `VideoOS.*.dll` in a plugin folder. The host already loads them; a second copy breaks binding. `deploy.ps1` aborts if the build produced any.

`plugin.def` (client) must keep `load env="SmartClient Administration"` in **one** string — MIP only reads the first `<load>`. The server plugin must stay `load env="Service"` so it never loads in a client.

## Server behaviors the code is written around

These are measured, not documented by Milestone. Changing them without a harness failure against a real server is a regression.

1. Unused recurrence fields are still validated, and servers disagree. Emit a valid placeholder everywhere (`MaxOccurrencesPlaceholder` and friends). A write test against one server does not prove the next.
2. A forbidden read comes back **empty**, not as an error. Never conclude "deleted" from emptiness (`SaveStatus.NotVisible`). Ask the Event Server component.
3. `AppointmentRootId` is reissued on every read. Use client keys (`ScheduleEntry.ClientKey`) for identity across load/save.
4. A delete that misses its target still returns `Success`. Re-read after every save and compare to what was requested.
5. `"24:00:00"` is 24 **days**. Whole-day intervals are `00:00–23:59`. `TimeProfileRepository.MaxDuration` is the only place that decides that.
6. One-off dates are removed by start-time ticks, not the `<ticks>-<ordinal>` handle `RemoveAppointment()` returns. Set the selection on the same task object and `Execute()` with no read in between.

Day mask: Sunday = 1 … Saturday = 64.

## UI and copy

Operator-facing strings, dates, week numbers and help are Swedish. Identifiers and comments are English.

Help lives as data in `Model/HelpTopics.cs`, not in XAML. Check every claim against the control that implements it before changing help. Self-description (name, version, developer) is `Model/PluginInfo.cs` reading the assembly.

`InternalsVisibleTo` is `TimeProfileEditor.Harness` only. A throwaway WPF renderer must use that assembly name or it cannot see internals.

## Build

```powershell
dotnet build TimeProfileEditor.sln
.\build\build-installer.ps1          # client MSI + dist\Diagnostik  (needs WiX 5)
.\build\build-server-installer.ps1   # Event Server MSI — different machine, own script on purpose
.\build\deploy.ps1                   # copy client files; run PowerShell as Administrator
```

Do not fold the two installer scripts together. Closing Smart Client and Management Client before install avoids a locked DLL and a reboot prompt.

## Test

```powershell
dotnet run --project tests\TimeProfileEditor.Harness -- --server http://localhost
dotnet run --project tests\TimeProfileEditor.Harness -- --diag
```

`--write` creates and mutates a junk profile `TEST - Harness`. Lab servers only. Never add a `.cmd` in `dist\Diagnostik` that reaches `--write` — only read-only entry points may be a double-click away on a customer machine.

`--cleanup` removes the junk profile (left behind on purpose after a failed write run).

There is no in-repo unit-test project. Behaviour against Management Server is the harness. After changing repository, protocol, security or save/diff logic, run the harness if a lab server is available; do not invent a mock that pretends the six behaviours above.

## First-install order (do not skip)

1. Client MSI on the Management Client machine → start Management Client once (registers the namespace).
2. Grant permissions under Roles → Tidsprofiler.
3. Client MSI on Smart Client machines.
4. On Expert / Professional+ (or any role without configuration-write): Event Server MSI on the Event Server machine.
