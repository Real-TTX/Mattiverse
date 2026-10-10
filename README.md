<div align="center">

<img src="src/Mattiverse.Web/wwwroot/img/logo.svg" width="96" alt="Mattiverse" />

# Mattiverse

**Install, update and back up your Docker apps. One launcher for the whole Mat universe.**

A home screen for your Docker host: pick an app in the store, Mattiverse installs it, gives it a
domain, keeps it up to date and backs it up. It connects your reverse proxy ([Matcad](https://github.com/Real-TTX/Matcad)),
your DynDNS client ([Matddns](https://github.com/Real-TTX/Matddns)) and your Docker manager
([MatDock](https://github.com/Real-TTX/MatDock)) with the rest. One container, one volume, no cloud.

</div>

![The home screen: installed apps as icons, widgets for the state of the host](docs/images/home.png)

---

## What this is about

Self-hosting usually means a terminal, a pile of `docker-compose.yml` files and a browser full of
`:8080` tabs. Mattiverse is a calm layer on top: a launcher that looks like a phone, a **store** with
one-click installs, a **setup wizard** that asks three questions (which reverse proxy, which DynDNS
client, which Docker management) and wires the answers together, and **backups** you can actually restore.

It is the small, mobile-friendly sibling to **MatOS**. Where MatOS is a desktop with windows, Mattiverse
shows one app at a time, like a website or a phone. Everything it creates is a plain `docker compose` stack, so
you can always take over with `docker compose` or [MatDock](https://github.com/Real-TTX/MatDock).

## At a glance

**Launcher**
- Installed apps as **icons on a home screen** with a status dot, a search field and widgets (apps running, last
  backup, activity, Docker)
- **One app open at a time**: the app fills the screen with a bar to go back (apps that forbid being embedded
  open in a new tab)
- Account, theme (**System / Light / Dark**) and sign-out at the **bottom left** of the menu; on a phone the menu
  is a drawer

**Setup wizard** (first start, and again whenever you like)
- Which **reverse proxy**? Matcad or Nginx Proxy Manager
- Which **DynDNS client**? Matddns
- Which **advanced Docker management**? MatDock or Portainer
- Optional **domain** for your apps: with Matcad every app gets `app.example.com` automatically

**Store**
- Catalog of your Mat apps (MatMail, MatFile, MatPaper, MatPlay, MatSplit, Matmon, MatCMS, Matdo, MatBu,
  MatTrakr) and the infrastructure apps
- **Passwords are generated** and shown (hidden until you ask) on the app's page
- Add your **own apps** by dropping a compose file into `/data/catalog` (see [docs/CATALOG.md](docs/CATALOG.md))

**Operations**
- **Install, update, start, stop, restart, uninstall**: every long task is a job with a **live log**
- **Update** pulls newer images, re-creates only what changed, and backs up first if you want
- **Backups**: compose files + settings + all volumes, optionally with the app stopped (consistent databases),
  **schedules with retention**, restore even after the app was uninstalled, download as `.tar`
- **Shared links** let someone without an account open one app

**Users**
- Local sign-in, **roles Admin / User / Anonymous (shared link)**, sessions that **survive a restart**
  (Microsoft Entra ID is prepared, see [Status](#status))

## Screenshots

### The store and the setup wizard

| Store | Setup wizard |
|---|---|
| ![Store](docs/images/store.png) | ![Setup wizard: which reverse proxy](docs/images/wizard.png) |

The wizard warns when a port it needs is taken (here: another proxy holds 80 and 443) and leaves
alone what is installed already.

### Apps, installs with a live log

| An app | The install log |
|---|---|
| ![App details with credentials, containers and actions](docs/images/app-details.png) | ![Live log of an install](docs/images/install-log.png) |

Buttons read from positive to negative: **Open, Stop, Restart, Update, Back up**, then a gap, then **Uninstall**.

### Backups

| History | Schedules |
|---|---|
| ![Backup history](docs/images/backups.png) | ![Backup schedules](docs/images/backup-schedules.png) |

Every list has its toolbar (search, filters, sort) on top, its actions below and pagination directly under the table.

### Light theme and phone

| Light | Phone | Phone menu |
|---|---|---|
| ![Light theme](docs/images/home-light.png) | ![Phone home screen](docs/images/mobile-home.png) | ![Phone menu](docs/images/mobile-menu.png) |

## Quick start

One command. The Docker socket is how Mattiverse installs and manages your apps.

```bash
docker run -d --name mattiverse --restart unless-stopped -p 8020:8020 \
  -v mattiverse-data:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  ghcr.io/real-ttx/mattiverse:latest
```

or with Compose:

```yaml
name: mattiverse

services:
  mattiverse:
    image: ghcr.io/real-ttx/mattiverse:latest
    container_name: mattiverse
    restart: unless-stopped
    ports:
      - "8020:8020"
    volumes:
      - mattiverse-data:/data
      - /var/run/docker.sock:/var/run/docker.sock

volumes:
  mattiverse-data:
```

Open **http://localhost:8020**. The first start asks for the administrator account and then walks you through the
setup wizard. An update is `docker compose pull && docker compose up -d`; everything lives in the `mattiverse-data` volume.

| Tag | Built from | Use it for |
|---|---|---|
| `ghcr.io/real-ttx/mattiverse:latest` | `main` | releases |
| `ghcr.io/real-ttx/mattiverse:nightly` | `dev` | the newest features |

> **The Docker socket is root on the host.** Mattiverse needs it, so keep the UI off the public internet:
> put it behind your reverse proxy with the apps' own logins, or on a VPN.

### Settings that matter

| Variable | Default | Meaning |
|---|---|---|
| `Mattiverse__DataPath` | `/data` | Data folder: database, settings, compose files, backups |
| `Mattiverse__SessionLifetimeDays` | `30` | How long a sign-in stays valid |
| `Mattiverse__NetworkName` | `mattiverse` | Shared Docker network of the reverse proxy and the apps' UIs |
| `Mattiverse__HostPortRangeStart` / `End` | `8100` / `8999` | Pool for app ports when the preferred port is taken |
| `Mattiverse__Admin__Seed` | `false` | Create the admin from `__Username` / `__Password` (headless installs; the password must be changed at the first sign-in) |
| `Mattiverse__DockerHost` | – | Use another Docker endpoint than the mounted socket |
| `MATTIVERSE_PORT` | `8020` | Host port in the compose files |
| `TZ` | – | Time zone for schedules and the apps |

## How it works

```
/data
├─ mattiverse.db        SQLite: users, sessions, apps, jobs, backups, schedules, shared links
├─ config/system.json   JSON: name, domain, the wizard's choices
├─ dataprotection-keys/ keys for the cookies and the secrets (so sessions survive restarts)
├─ catalog/             your own app definitions (*.yml)
├─ apps/<app>/          docker-compose.yml, docker-compose.override.yml (generated), .env
└─ backups/<app>/<stamp>/   manifest.json + one .tar.gz per volume
```

- An app is a **compose project** named `mattiverse-<app>`. Mattiverse writes the catalog's compose file, a `.env` with
  your answers and a small generated override (container names, labels, the shared network, the host port of the UI).
  Every container carries the compose project label, so **MatDock** and `docker compose ls` see the stacks too.
- Containers of an app are only attached to the **shared network** `mattiverse` when they must be reachable by
  the proxy (the UI service); databases stay private to their stack.
- With **Matcad** and a domain, every install creates the route `<app>.<domain>` through Matcad's REST API;
  uninstalling removes it again. Mattiverse itself gets a route too.
- **Backups** copy each volume with a throw-away `busybox` container (`tar` over the socket) and store the compose
  files and the image digests in the manifest, so a backup is restorable on a fresh Mattiverse.
- The design decisions (why backups live here and not in MatDock, where the compose files are, how this relates to
  MatOS) are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Roles

| Role | Can |
|---|---|
| **Admin** | everything: store, installs, updates, backups, users, settings, activity |
| **User** | see and open the apps (no credentials, no actions) |
| **Anonymous** | open the one app behind a **shared link** (`/s/<token>`); links expire and can be revoked |

## Development

Stack: ASP.NET Core 10 (Razor Pages), C#, SQLite via EF Core, the `docker` CLI against the mounted socket, plain
JavaScript. No Node build step.

```bash
dotnet build Mattiverse.slnx          # 0 warnings expected
dotnet test                           # unit tests (catalog, installer files, backups, schedules, parsing)
./scripts/redeploy.ps1                # rebuild the container and redeploy the dev stack
./scripts/redeploy.ps1 -Fresh         # ... with a clean data volume (the first-run setup appears again)
```

The dev stack is the app plus a SQLite web viewer (the SQLite counterpart of pgAdmin) on
`http://localhost:8021`. In `Development` a ready-made account **`tester` / `Tester123!`** exists, so testing never
touches the real administrator. `MATTIVERSE_PORT` changes the host port when 8020 is taken.

Testing means rebuilding the container and redeploying the stack, nothing else.

### Branches and versions

| Branch | Purpose | Image tag | Version |
|---|---|---|---|
| `main` | Release | `latest`, `release`, `<version>` | `<major>.<minor>.<build>-<yyyyMMdd>` |
| `dev` | Development | `nightly`, `<version>` | `nightly-<build>-<yyyyMMdd>` |
| local | – | – | `local-<yyyyMMdd>` |

`major` and `minor` live in [`Directory.Build.props`](Directory.Build.props); the build number is the run number of the
GitHub Action, which also builds the multi-arch image (`amd64`, `arm64`).

## Status

| Area | Status |
|---|---|
| Launcher, store, wizard, themes, mobile layout | ✅ |
| Install / update / uninstall / start / stop with live logs | ✅ |
| Backups: manual, scheduled, retention, restore, download | ✅ |
| Local users, roles, persistent sessions, shared links | ✅ |
| Matcad routes per app, base domain | ✅ |
| Microsoft Entra ID sign-in | planned (the login is already behind `AuthService` / `SessionService`) |
| Remote backup targets (SMB, off-site) | use MatDock for now ([docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)) |
| Provisioning Matddns sources and MatDock environments | needs small changes there ([docs/INTEGRATION.md](docs/INTEGRATION.md)) |

## License

No license file is included yet. If you intend others to reuse this, add a `LICENSE` (for example MIT) at the repo root.
