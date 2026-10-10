# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, persistence,
interfaces or major data flow change.

## What Mattiverse is

A launcher (iOS/Android look, one app open at a time) that **installs, updates and backs up Docker apps**. It is a thin layer on top of
three **engines** that own the configuration and show it in their own UI: **MatDock** (stacks, backups, schedules), **Matcad** (routes,
certificates, DNS providers) and **Matddns** (IP sources, DNS records, rules). Mattiverse calls their REST APIs (`docs/ENGINES.md`); the wizard
installs them and asks once for the domain and the DNS account. They are fixed parts, not options. Every app is a compose project.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages** + TagHelper controls, EF Core + **SQLite**, the `docker` CLI against the mounted
socket, plain JavaScript (no build step), Docker-first. Web port **8030**.

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the design: it records the decisions (the engines own the
configuration, reading goes to Docker and changes to the engines, catalog as data, ...). [docs/ENGINES.md](docs/ENGINES.md) is the API contract
with MatDock, Matcad and Matddns, [docs/CATALOG.md](docs/CATALOG.md) the format of a catalog entry,
[docs/INTEGRATION.md](docs/INTEGRATION.md) the state of the sibling repositories (the `mattiverse-integration` branches).

## Build / run / test

- **Build:** `dotnet build Mattiverse.slnx` (note: `.slnx`). SDK 10 (see `global.json`). Expected: 0 warnings.
- **Unit tests:** `dotnet test` (xunit; no Docker needed: catalog parsing, generated compose files, variables, ports, schedules,
  `docker ps` parsing).
- **Run / test the app: always rebuild the container and redeploy the stack** (project rule): `./scripts/redeploy.ps1`
  (`-Fresh` wipes the volume, `-Port` changes the host port). Dev stack: `docker-compose.dev.yml` (app + `sqlite-web` on 8021).
  In `Development` the account `tester` / `Tester123!` exists.
- **Release stack:** `docker-compose.yml`, image `ghcr.io/real-ttx/mattiverse`, one service and one volume: keep it that simple.
- **Real tests are end-to-end against Docker.** The pattern that worked: install apps through the UI with Playwright
  (installed Edge, see the `screenshots` skill), then check with `docker ps` / `docker exec` / `curl`. Remember
  `MSYS_NO_PATHCONV=1` for Git Bash arguments that start with `/`.
- **The development PC has all Docker address pools used up** (`all predefined address pools have been fully subnetted`).
  To test installs there, put an explicit `networks: default: ipam: config: - subnet: 10.253.x.0/24` into the catalog copy
  under `/data/catalog` and give each app its own subnet, and set `Mattiverse__NetworkSubnet` for the shared network. The web port is 8030
  (8020 is Schulio's); `MATTIVERSE_PORT` changes the host port.
- **Testing the engines:** run a second Mattiverse (`docker run`, own volume, port 8130) with catalog copies in `/data/catalog` that use the locally built
  images (`matdock:mattiverse`, `matcad:mattiverse`, `matddns:mattiverse`, built from the `mattiverse-integration` branches), `pull_policy: never`
  (compose cannot pull a local image), other host ports (the PC has a real MatDock on 4455, Matcad on 4433) and own subnets. Do not touch the
  other containers on the PC.
- **Changing engine behaviour:** the engines' own repositories are where it happens (branch `mattiverse-integration`); keep `docs/ENGINES.md` and the
  clients (`MatDockClient`, `MatcadClient`, `MatddnsClient`) in step with them.

## Map

```
src/Mattiverse.Core        domain, no web types
  Entities/ Data/            EF entities (AuditableEntity, soft delete), DbContext, migrations
  Apps/                      AppService (install/update/uninstall/lifecycle), ComposeRenderer (catalog entry -> the one compose file),
                             ComposeFiles, ComposeRunner, AppReconciler (adopt MatDock stacks), AppVariables, PortPlanner
  Engines/                   IStackEngine: LocalComposeEngine (own docker compose) and MatDockEngine; StackEngines picks one;
                             EngineClient/EngineLocator (address + API key of an installed engine), MatDockClient, EngineIds
  Backups/                   IBackupProvider: LocalBackupProvider (BackupService, own runner) and MatDockBackupProvider; BackupHub merges
                             them (ids local:<id>, matdock:<id>); BackupSchedulerService runs the local schedules
  Catalog/                   CatalogParser/Service/Models + apps/*.yml (embedded)
  Docker/                    DockerCli (process wrapper), DockerService (typed ops, parsing)
  Integrations/              MatcadClient, MatddnsClient, ProxyPublisher (routes, wildcard), DnsPublisher (records), IAppProvisioner
  Jobs/                      JobService/Queue/RunnerService, AppJobs (the work per job kind), JobContext/log
  Auth/ Users/ Security/     login, sessions, PBKDF2 hasher, secret protector
  Configuration/             options, AppPaths, JsonConfigStore, SystemConfig
src/Mattiverse.Web         Razor Pages, controls, JS/CSS, Program.cs
  Controls/                  mat-table, pagination, mat-field, mat-form-buttons, tab-bar, app-icon, icon
  Pages/                     Index (launcher), Apps/, Store/, Backups/, Jobs/, Settings/, Wizard/, Users/, Account/, Share/
  Infrastructure/            cookie session validation, setup guard, DB bootstrap, API endpoints, health check
  Support/                   AppUrls, FrameProbe, AppStatus, Format
tests/Mattiverse.Tests
```

## Conventions

**Code**
- Microsoft C# guidelines, file-scoped namespaces, `.editorconfig`. Few nested types; several business functions per class are fine.
- Long Docker work never runs in a request: queue a `Job` (`AppJobs.*`) and let the pages poll `/api/jobs/<id>/log`.
- **Read from Docker, change through the engines.** Status, containers and logs come from `docker` (labels); installs, updates, backups, routes and
  DNS records go through the engine's API with `managedBy: mattiverse`. Never write to an engine without that marker; never delete what Mattiverse did not create.
- Engine clients share the request's `DbContext` (the locator reads the app): never call two of them in parallel.
- Anything that parses CLI output has a unit test (a stopped container has an empty last field: never `Trim` such lines).
- Generated files use LF. A single quote must not appear in variable values (they are written single-quoted to `.env`).

**Database**
- JSON for configuration (`config/system.json`), SQLite for logic. Tables are PascalCase and singular; the key is `Id` (64 bit);
  rows have `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`, `UpdateState` (0 deleted, 1 created, 2 updated);
  security keys are a `Token` UUID (`UserSession`, `ShareLink`). New schema: `dotnet ef migrations add <Name> --project src/Mattiverse.Core
  --startup-project src/Mattiverse.Web --output-dir Data/Migrations`.

**UI**
- CRUD = a list plus a separate page for create/edit. Lists: toolbar (search, filter, sort) on top, list actions below and
  left-aligned, delete spaced apart, pagination directly under the table (`<mat-table>` slots). Buttons in one line, positive
  to negative: Save, Back, (space) Delete. Icons where they help. Dependent fields only when needed (`data-show-if`).
- Account, logout and the theme (System/Light/Dark) are at the **bottom left of the menu**. Scrollbars are hidden until scrolling.
- Mobile matters: check phone width (390 px) for every new page; secondary table columns get `hide-sm`.
- After a change to a CSS/cshtml file the container must be rebuilt (they are baked into the image).
- Take screenshots of real data only; never real user data in `docs/images`.

**Git**
- Commit and push **under the user's name**, never add a Claude/Anthropic co-author trailer or "generated with" line.
- Branches: `main` = release, `dev` = development. Versions: release `<major>.<minor>.<build>-<date>`, development
  `nightly-<build>-<date>`, local `local-<date>` (`Directory.Build.props`, `.github/workflows/build.yml`).
