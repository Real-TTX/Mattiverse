# Architecture and decisions

This page records the questions that shaped Mattiverse and the answers, so they do not have to be argued again.

## What Mattiverse is: a layer on top of three engines

Mattiverse is **not** a second Docker manager with its own copy of everything. It is the thin, friendly layer on top of three programs
that each **own** one part of the configuration:

| Engine | Owns | Mattiverse adds |
|---|---|---|
| **MatDock** | stacks (compose + env), containers, backups, schedules, updates | the store, one-click installs, the launcher |
| **Matcad** | routes, certificates, DNS providers for certificates, logins | a route `app.example.com` for every app |
| **Matddns** | sources of the IP, DNS records, rules | the DNS record `app.example.com` for every app |

Mattiverse itself keeps what no engine knows: users, roles and sessions, shared links, the catalog, the wizard's choices, which catalog
entry an app came from, and where it sits on the home screen. Everything else lives in the engines and **is visible and editable there**: open
MatDock and the apps are stacks marked *Managed by Mattiverse*, open Matcad and they are routes, open Matddns and they are records.

The three are **fixed parts** of Mattiverse (the wizard installs them; they cannot be uninstalled from Mattiverse except a failed install, to start over).
Other tools in the store (Nginx Proxy Manager, Portainer, ...) are plain apps without deep integration. The contract between Mattiverse and the
engines is [ENGINES.md](ENGINES.md).

| | Mattiverse | MatOS | MatDock |
|---|---|---|---|
| Idea | A launcher: one app at a time, like a phone | A desktop in the browser: windows, taskbar | A Docker manager for many hosts over SSH |
| Strength | Installing the Mat apps, the setup wizard, mobile | Rich desktop, file explorer, task manager | Stacks, volumes, backups, terminal, many hosts |

## How an app is installed, updated and removed

1. The page validates the form and records the app (`InstalledApp`, state *installing*); a **job** does the rest (see below).
2. `ComposeRenderer` turns the catalog entry into **one compose file**: the entry's services plus what only Mattiverse knows (container
   names, labels, the shared network, the published port of the UI) and an `x-matdock` block, so MatDock's launchpad shows the stack as an app.
   Next to it a `.env` with the answers of the form.
3. `StackEngines` picks the engine that **runs** the stack: **MatDock** once its API answers (`apiVersion >= 1`), else Mattiverse's own
   `docker compose` runner (`LocalComposeEngine`). MatDock itself is always deployed by the local runner, because nothing can deploy it before it exists.
   The app remembers its engine (`InstalledApp.Engine`).
4. The stack is created in MatDock (`PUT /stacks/mattiverse-<app>`, `managedBy: mattiverse`) and deployed; the output of MatDock's operation is
   forwarded to the job's log.
5. The provisioners and publishers then connect it: the route in Matcad, the record in Matddns.

**Reading is not going through the engines.** Status, containers and logs always come straight from Docker (every container carries the
labels `mattiverse.managed`, `mattiverse.app`, `mattiverse.instance`, `mattiverse.service`), so the launcher stays correct and fast even
when an engine is down. Only *changes* go through an engine.

**The user may change the stack in MatDock.** Mattiverse stores a semantic hash of the compose it deployed (`ComposeSha`: parsed YAML, key order
and formatting do not matter). An update compares it with the stack as MatDock has it now; if they differ the update **keeps the user's version**
(pull and re-create, no rewrite) and says so in the log. In the Files tab the app shows the stack as its engine has it.

**Without an engine API** (an older released image, or MatDock not installed yet) everything keeps working through the local runner; the
Engines tab in the settings says which engine is connected.

**After a lost database or a restore in MatDock**, `AppReconciler` takes the stacks that MatDock holds with `managedBy: mattiverse` back as apps:
the stack carries everything (catalog block, compose, variables).

## Domain and DNS

The wizard asks **once** for the domain of the apps and the account of the DNS provider (Cloudflare, Hetzner, Netcup: the providers that both Matcad and
Matddns support). The credentials are stored protected (Data Protection) and handed to both engines:

- **Matcad** gets the provider and a wildcard route `*.domain` with it: one certificate through the DNS challenge, no open port 80 needed. Every
  app gets a route `app.domain` served from that certificate. Without a DNS account only the single routes exist (HTTP challenge).
- **Matddns** gets a source *public IP*, a domain group with a record for every app and a rule per record, so the record follows the home IP.

When the domain or the account changes, or from the Engines tab (*Connect again*), the job `ConnectEngines` brings both in line again and removes what
Mattiverse created earlier and does not want any more (routes of an old domain).

## Where are the backups?

**In MatDock**, which owns backups and schedules: a bundle of all volumes, the compose and the `.env`, with retention. They show up in MatDock's
backup and schedule lists and are started from Mattiverse's *Backups* page (`BackupHub`, ids `matdock:<id>`).

The apps that Mattiverse runs itself (MatDock, and everything while MatDock does not answer yet) are backed up by Mattiverse's own runner
(`LocalBackupProvider`, ids `local:<id>`): each volume is copied with a throw-away `busybox` container, the manifest keeps the compose files, the
variables and the image digests, so a local backup restores into a fresh Mattiverse. The two lists are merged on one page; an *All apps* schedule runs
in MatDock, a schedule for an app runs where the app runs.

- **Consistent by default.** Containers are stopped while the volumes are copied and started again afterwards, whatever happens. A live copy is
  possible and marked as such.
- **Restore** first backs up the current state (switchable) and re-creates the app even when it was uninstalled.
- **Download** as a `.tar` to copy a backup off the host. Remote targets (SMB, NAS) are MatDock's backup targets.
- Limits: backups contain secrets (the `.env`), so protect the location like the volumes; no encryption.

## Where are the compose files?

Of the stacks that MatDock runs: **in MatDock** (below its data volume), one text per stack, which is what the user sees and edits there.
Of the stacks that Mattiverse runs itself: on the Mattiverse data volume, `/data/apps/<app>/` with `docker-compose.yml` (the rendered file) and
`.env`, so `docker compose -p mattiverse-<app> -f ... up -d` works by hand.

**Rule for catalog entries:** use named volumes, not relative bind mounts. `docker compose` runs inside a container (Mattiverse's or MatDock's), so a relative
path would resolve inside that container and not on the host.

## Where does the catalog live?

**In Mattiverse** (`src/Mattiverse.Core/catalog/apps/*.yml`, embedded, plus `/data/catalog/*.yml`). A catalog entry is a compose file with an
`x-mattiverse` block ([CATALOG.md](CATALOG.md)): data, not code. MatOS keeps its catalog in C# code, which makes every new app a release; here it is a file.
A user file with the same `id` replaces the built-in entry. The three engines carry `core: true` and a `provision` key that names what connects them.

All apps and engines share the Docker network `mattiverse` with Mattiverse itself (it attaches itself on start), so they reach each other by container name
(`mattiverse-<app>-<service>`). If Docker's address pools are used up, `Mattiverse__NetworkSubnet` gives the network an explicit subnet.

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
- The engines' API keys are generated by Mattiverse when it installs them, stored with the app's variables (protected) and sent as `X-Api-Key`.
  An engine without a key answers `503`, so its API is never open.

## Database conventions

SQLite (`mattiverse.db`) holds the logic, JSON (`config/system.json`) the configuration.

- Tables are PascalCase and singular (`User`, `UserSession`, `InstalledApp`, `Job`, `Backup`, `BackupSchedule`, `ShareLink`).
- The primary key is always `Id` (64 bit). A table with a security key has a `Token` (UUID): `UserSession`, `ShareLink`.
- Every row has `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId` and `UpdateState` (0 deleted, 1 created, 2 updated).
  `Remove` becomes a soft delete and a global query filter hides deleted rows; unique indexes ignore deleted rows.
- Schema changes are EF Core migrations (`src/Mattiverse.Core/Data/Migrations`), applied at start.
- `Backup` and `BackupSchedule` only hold the backups and schedules of Mattiverse's own runner; those of MatDock live in MatDock.

## UI conventions

Mattiverse uses **MatUi**, the UI toolkit of [MatDesign](https://github.com/Real-TTX/MatDesign), so that it looks and behaves like the other Mat apps; the rules are MatDesign's rulebook.

- **The toolkit is a copy** in `src/Mattiverse.Web/wwwroot/matui` (CSS, one script, the icon sprite): the app works offline and as a PWA, and nothing is loaded from another site.
  `scripts/update-matui.ps1` brings a newer release from MatDesign. The app adds only what is its own: `mv-*` classes (app icons, home tiles, the app frame, the wizard) in `mattiverse.css`
  and a small script (clock, job log, wizard).
- **Shell:** the layout draws the menu (one menu for a user; Administration is a second menu behind the switcher for an administrator; Settings is a submenu), the user menu, the 3-state theme button,
  the version and the breadcrumb. Sign-in, setup, shared links and errors have no menu (`_Bare`). The theme is kept on the account (`/api/theme`) and in the browser.
- **Controls:** the tag helpers `<icon>` (a use of the sprite), `<app-icon>`, `<mat-field>` and `<tab-bar>` and the partials `_Toolbar`, `_Pager`, `_ValidationSummary` and `_StatusPill` are the one code base for them.
- **Lists:** the page model builds a `ListView` (the rows of the page, a `ToolbarModel`, a `PagerModel`) from the query string. The toolbar has a search field, Filter and Sort open dialogs. Every list has its toolbar
  on top, the pager directly under the table and its actions below, left-aligned.
- **Forms:** CRUD pages are a list and a separate page for create/edit. Buttons stand in one line from positive to negative: *Save, Back, (space) Delete*. The page validates (`novalidate`: no bubbles of the browser);
  a summary at the top links to the fields. After a save the page redirects and shows a toast. Dependent fields are shown only when needed (`data-show-if`).
- **The logo** stays the planet with its orbit, not the flat glyph tile that MatDesign's rule IDENT-03 describes: the owner prefers it.
- Mobile first where it counts: a drawer for the menu, tables become cards, dialogs are full screen, safe-area insets for notches.

## What is deliberately not here

- **No `mssql-server` / `pgadmin` service.** The project template names them; with SQLite neither is needed. The release
  stack is the single service `mattiverse`; the dev stack adds a SQLite web viewer.
- No multi-host management (that is MatDock), no file explorer or terminal (MatOS / MatDock).
- No deep integration of alternatives (Nginx Proxy Manager, Portainer): they are installed as plain apps.
