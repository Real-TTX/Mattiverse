# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, persistence,
interfaces or major data flow change.

## What Mattiverse is

A launcher (iOS/Android look, one app open at a time) that **installs, updates and backs up Docker apps** and connects the
Mat universe: a setup wizard asks for the reverse proxy (Matcad), the DynDNS client (Matddns) and the advanced Docker
management (MatDock). Everything it creates is a plain `docker compose` project.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages** + TagHelper controls, EF Core + **SQLite**, the `docker` CLI against the mounted
socket, plain JavaScript (no build step), Docker-first. Web port **8020**.

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the design: it records the decisions (compose files on the volume,
backups integrated, catalog as data, ...). [docs/CATALOG.md](docs/CATALOG.md) is the format of a catalog entry,
[docs/INTEGRATION.md](docs/INTEGRATION.md) what the sibling projects would need.

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
  under `/data/catalog` and give each app its own subnet; and use a free host port (`MATTIVERSE_PORT`, 8020 is Schulio's).

## Map

```
src/Mattiverse.Core        domain, no web types
  Entities/ Data/            EF entities (AuditableEntity, soft delete), DbContext, migrations
  Apps/                      AppService (install/update/uninstall/lifecycle), ComposeFiles, ComposeRunner, AppVariables, PortPlanner
  Backups/                   BackupService (backup/restore/retention), BackupManifest, BackupSchedulerService
  Catalog/                   CatalogParser/Service/Models + apps/*.yml (embedded)
  Docker/                    DockerCli (process wrapper), DockerService (typed ops, parsing)
  Integrations/              MatcadClient, ProxyPublisher, IAppProvisioner
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
