# Architecture and decisions

This page records the questions that shaped Mattiverse and the answers, so they do not have to be argued again.

## What Mattiverse is, and how it relates to MatOS and MatDock

| | Mattiverse | MatOS | MatDock |
|---|---|---|---|
| Idea | A launcher: one app at a time, like a phone | A desktop in the browser: windows, taskbar | A Docker manager for many hosts over SSH |
| Strength | Installing, updating and backing up the Mat apps, the setup wizard, mobile | Rich desktop, file explorer, task manager | Stacks, volumes, terminal, many hosts |
| Where it stops | No file explorer, no terminal, one host | Compose files live in `/tmp`, backups miss the compose file | No API, no catalog |

Mattiverse takes the good ideas of MatOS (an app is a compose file with a metadata block, containers carry labels,
whole-app backups) and fixes the weak spots: the compose files are **persistent**, a backup contains **everything
needed to restore** and databases can be copied **consistently**. MatDock stays the tool for the advanced cases.

## Where are the compose files?

**On the Mattiverse data volume, one folder per app:** `/data/apps/<app>/`.

```
docker-compose.yml            the catalog entry, unchanged (its x-mattiverse block is ignored by compose)
.env                          the answers of the install form and a few values Mattiverse adds (MV_SLUG, TZ, ...)
docker-compose.override.yml   generated: container names, labels, shared network, the published port of the UI
```

Why not the database, or `/tmp` as MatOS does: files on the volume survive an update of Mattiverse (`pull && up -d`), are
easy to read and back up, and `docker compose -p mattiverse-<app> -f ... up -d` works by hand if Mattiverse is ever not
available. The database only stores what the UI needs (names, ports, state, credentials).

**Compatibility with MatDock and the other tools:** every container has the label
`com.docker.compose.project=mattiverse-<app>`, so MatDock lists the stacks as *discovered* and can start, stop and
restart them. Adopting a stack in MatDock reads the compose file from the host path in the label; that path is inside the
Mattiverse volume, so adoption needs MatDock to read it through the volume (see [INTEGRATION.md](INTEGRATION.md)).
Catalog entries are valid compose files, so MatDock's own *Apps* feature (a template is a compose text) can import
them as they are.

**Rule for catalog entries:** use named volumes, not relative bind mounts. Mattiverse runs `docker compose` inside its
container, so a relative path would resolve inside that container and not on the host.

## Where are the backups: Mattiverse or MatDock?

**Integrated in Mattiverse**, with MatDock as the tool for the advanced cases.

Reasons:
- **MatDock has no API.** It cannot be driven from outside today; every MatDock feature is a page with a cookie login.
  Delegating backups would first need a new `/api/v1` there.
- The wizard promises that Mattiverse alone can install **and back up**. Having to set up a second tool for the second
  half would break that.
- What is needed for the simple case is small: copy the volumes, keep the compose files, restore.

What a backup is (`/data/backups/<app>/<timestamp>/`):

```
manifest.json         app, catalog entry, the three compose files, services with image digests, volume list
volumes/<volume>.tar.gz
```

- **Consistent by default.** The containers are stopped while the volumes are copied and started again afterwards, whatever
  happens (a `finally`). Databases (PostgreSQL in MatMail, MatPaper, Matdo) are therefore consistent without
  dump tooling. A live copy is possible and marked as such in the list.
- **Restorable anywhere.** The manifest holds the compose files and the variables, so a backup restores into a fresh
  Mattiverse (the app is re-created) and keeps working after the app was uninstalled. A restore first backs up the
  current state (switchable).
- **Schedules** are daily or weekly with a retention count (only *scheduled* backups are pruned, never manual ones).
- **Download** as a `.tar` to copy a backup off the host. Remote targets (SMB, NAS) are what MatDock already does: mount
  the share at `/data/backups` or use MatDock's backup targets for the volumes.

Limits that are on purpose for now: the backup folder contains secrets (the `.env`), so protect it like the
volume; images are recorded by digest but not exported; there is no encryption.

## Where does the catalog live?

**In Mattiverse** (`src/Mattiverse.Core/Catalog/apps/*.yml`, embedded, plus `/data/catalog/*.yml`). A catalog entry is a
compose file with an `x-mattiverse` block ([CATALOG.md](CATALOG.md)): data, not code. MatOS keeps its catalog in C#
code, which makes every new app a release; here it is a file. A user file with the same `id` replaces the built-in entry.

## Reverse proxy, DynDNS, Docker management

The wizard asks for three **roles**; every catalog entry has a `role` (`proxy`, `ddns`, `docker`, `app`).

- **Reverse proxy.** With **Matcad**, Mattiverse calls its REST API (`X-Api-Key`, generated per install and kept
  encrypted with the apps' variables): sets the base domain, creates a route `<app>.<domain>` →
  `http://mattiverse-<app>-<ui service>:<port>` for each app and removes it on uninstall. Other proxies are installed but
  routes are manual.
- **DynDNS.** Matddns is installed with a generated admin password. It has no management API yet, so its
  sources and rules are set up in its own UI ([INTEGRATION.md](INTEGRATION.md)).
- **Docker management.** MatDock is installed with a generated admin password (`MatDock__Admin__Seed`).

All of them share the Docker network `mattiverse` with Mattiverse itself (it attaches itself on start), so Mattiverse and
the proxy reach the apps by container name (`mattiverse-<app>-<service>`).

## How a long task runs

Nothing slow runs inside a web request. The page validates and records, then queues a **job** (`Job` table): a single
background worker runs jobs one after the other (Docker operations on one host must not overlap), keeps the output in
memory while it runs (the page polls `/api/jobs/<id>/log`) and writes it to the database every few seconds and at the end.
A job that was running when the container stopped is marked failed on the next start, and so are the apps that were
being installed.

## Authentication and roles

- Local accounts, passwords with PBKDF2 (210,000 iterations) and a constant-time comparison; the sign-in does not reveal
  whether a user exists.
- A **session** is a row in `UserSession` (a `Token` UUID in the cookie). Sessions live in SQLite on the volume and the
  Data Protection keys are persisted next to it, so **sessions survive a container restart**.
- Roles: `Admin`, `User`, `Anonymous`. Anonymous is not an account: it is the visitor of a **shared link**
  (`/s/<token>`), which shows one app. Pages are secure by default (`FallbackPolicy` requires a user); the
  admin pages are listed in `Program.cs`.
- Microsoft Entra ID: the sign-in is isolated in `AuthService` and `SessionService`; adding an OpenID Connect scheme
  next to the cookie and mapping the claims to `User` is the planned step.

## Database conventions

SQLite (`mattiverse.db`) holds the logic, JSON (`config/system.json`) the configuration.

- Tables are PascalCase and singular (`User`, `UserSession`, `InstalledApp`, `Job`, `Backup`, `BackupSchedule`, `ShareLink`).
- The primary key is always `Id` (64 bit). A table with a security key has a `Token` (UUID): `UserSession`, `ShareLink`.
- Every row has `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId` and `UpdateState` (0 deleted, 1 created, 2 updated).
  `Remove` becomes a soft delete and a global query filter hides deleted rows; unique indexes ignore deleted rows.
- Schema changes are EF Core migrations (`src/Mattiverse.Core/Data/Migrations`), applied at start.

## UI conventions

- **Controls** are tag helpers with one code base (`src/Mattiverse.Web/Controls`): `<mat-table>` (toolbar, body,
  pagination, actions), `<pagination>`, `<mat-field>` / `<mat-form-buttons>`, `<tab-bar>`, `<app-icon>`, `<icon>`. Several of
  the same control can be on one page.
- **Lists:** toolbar (search, filters, sort) on top; list actions below, left-aligned, delete spaced apart; pagination
  directly under the table.
- **Forms:** CRUD pages are a list and a separate page for create/edit. Buttons stand in one line from positive to
  negative: *Save, Back, (space) Delete*. Dependent fields are shown only when needed (`data-show-if`).
- Scrollbars are hidden until something scrolls. Icons are inline SVG, no CDN.
- Mobile first where it counts: drawer menu, secondary table columns hide on phones, safe-area insets for notches.

## What is deliberately not here

- **No `mssql-server` / `pgadmin` service.** The project template names them; with SQLite neither is needed. The release
  stack is the single service `mattiverse`; the dev stack adds a SQLite web viewer.
- No multi-host management (that is MatDock), no file explorer or terminal (MatOS / MatDock), no remote backup targets yet.
