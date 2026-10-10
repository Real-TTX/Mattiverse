# Working with the other Mat projects

Mattiverse works with the **released images of your projects as they are today**. Everything below is what was found
while building it and what would make the connection smoother. Nothing here was changed in the other repositories.

Verified against the sources and the public images on 2026-10-10.

## What works today

| Project | Image used | What Mattiverse does |
|---|---|---|
| **Matcad** | `ghcr.io/real-ttx/matcad` + `matcad-caddy` (`:latest`) | Installs both containers (`caddy`, `matcad` on one network), sets `Matcad__ApiKey`, then uses the REST API to set the base domain and create/delete a route per app. Also creates the route for Mattiverse itself. |
| **Matddns** | `ghcr.io/real-ttx/matddns:latest` | Installs it with a generated `MATDDNS_ADMIN_PASSWORD`. Sources, domains and rules are set up in its UI. |
| **MatDock** | `ghcr.io/real-ttx/matdock:release` | Installs it with `MatDock__Admin__Seed=true` and a generated password, mounts the socket. Mattiverse's stacks appear there as *discovered*. |
| Mat apps | MatMail, MatFile, MatPaper, MatPlay, MatSplit, Matmon, MatCMS, Matdo, MatBu, MatTrakr | Installed from the catalog with their documented environment variables. |

Not in the catalog: **msBi** (no anonymously pullable image), **Matgate** (four containers plus a browser farm),
**Matstat** (local build and MSSQL), **Matcat** (the old name of Matcad).

## Suggested changes

Ordered by what they unlock. The first group is small and removes real friction.

### MatDock

1. **Publish `:latest`.** The workflow pushes `<version>` and the channel name (`release` / `nightly`) but never `latest`; the
   README and compose examples say `ghcr.io/real-ttx/matdock:latest`, which does not exist (`404`). MatMail's workflow tags
   `latest`, `release` and the version: copy that. Mattiverse uses `:release` meanwhile.
2. **README: `MatDock__Admin__Seed`.** The README's `MatDock__Admin__Username/Password` only apply when
   `MatDock__Admin__Seed=true` is set too (`DbBootstrapper`); without it the first start shows the setup page.
3. **An API.** MatDock has no REST/JSON API and no API key (cookie login only), so nothing outside can drive it. A small
   `/api/v1` with `X-Api-Key` like Matcad's (`stacks`, `backups/run`, `status`) would let Mattiverse hand the advanced backup
   cases (SMB targets, migrating volumes to another host) to MatDock instead of re-implementing them.
4. **A "local" environment when the socket is mounted**, so the app is useful right after Mattiverse installed it.
5. **Adopting Mattiverse stacks.** Adoption reads `com.docker.compose.project.config_files` through a throw-away container
   with a bind mount of the host path. Mattiverse keeps the files in a Docker volume, so the path does not exist on the host
   (the stack is listed and controllable, but cannot be adopted). Reading it from the volume
   (`/var/lib/docker/volumes/<volume>/_data/apps/<app>`) fixes that. MatDock's `Apps` feature can already import Mattiverse
   catalog entries, since they are plain compose files.

### Matcad

1. **`MATCAD_ADMIN_PASSWORD`.** The admin is always `admin`/`admin` and the setup page forces a change. An environment
   variable (like Matddns' `MATDDNS_ADMIN_PASSWORD`) would let Mattiverse hand out a real password from the start. Today it
   shows `admin`/`admin` with the note that Matcad asks to change it.
2. **An anonymous health endpoint** (`/healthz`, like Matddns). Mattiverse polls the authenticated `GET /api/v1/status`.
3. **`Matcad__Caddy__AdminUrl` is effectively ignored**: its default `http://caddy:2019` is not empty, so the settings file wins.
   Mattiverse relies on the compose services being named `caddy` and `matcad`; an environment override that works would
   remove that coupling.
4. **Publish `latest` on pushes to `main`**, not only on `v*` tags, or document the tag policy.

### Matddns

1. **A way to create the first working record without the UI**, for the wizard: either an environment switch that seeds a
   *Public IP* source, or a management API next to the push endpoints. Today only the `config.json` on the volume can be seeded,
   and it is read once at start.
2. **An `arm64` image.** Mattiverse, Matcad and MatBu are multi-arch; Matddns is `amd64` only, so it cannot be installed on a
   Raspberry Pi through the wizard.

### All apps that should open inside Mattiverse

Mattiverse shows one app at a time inside its own frame. These send `X-Frame-Options: SAMEORIGIN` or a CSP
`frame-ancestors` and are therefore offered with *Open in a new tab* instead: **Matcad, Matddns, MatDock, Matmon, MatSplit**.
MatFile, MatPlay and the nginx test app embed fine. An option on each app such as
`<APP>__AllowEmbeddingFrom=https://mattiverse.example.com` (sets `frame-ancestors` to that origin only) would let them run in
the frame without opening them to everyone. (Matcad's route option *Allow embedding* removes the header for the public route,
which is wider than needed; Mattiverse leaves it off.)

### Conventions that would make the catalog simpler

- **`<APP>_ADMIN_PASSWORD`** on every app (MatFile, MatMail, MatTrakr and Matddns have it; MatPlay, MatSplit, MatCMS, MatBu
  start as `admin`/`admin`; MatPaper, Matdo and Matgate use a first-visit wizard).
- **`/healthz`** returning `200` anonymously (MatPaper, MatPlay, MatCMS, Matdo, MatTrakr have none).
- **An `x-mattiverse` block in each project's own compose file**, so the catalog entry comes from the app's repo and cannot
  drift from it ([CATALOG.md](CATALOG.md)).
- **MatPaper**: the database password is the fixed `matpaper`; an environment variable for it would allow a generated one.

## Other findings

- **Port 8030**: the first choice, 8020, is the default of the *Schulio* project (`Schulio/docker-compose.yml`, `SCHULIO_PORT=8020`),
  which was running on the development PC. 8030 is used by no project in the development folder and was free on that PC.
  `MATTIVERSE_PORT` still changes the host port if it is ever taken.
- `Prompt-Docker-Init-Mattiverse.txt` and `Prompt-Docker-Init-MatDock.txt` in the development folder are verbatim copies of the
  MatSplit prompt (port 4774, "Projektname: MatSplit"). MatDock really uses 4455.
- Host ports used by more than one project (found while collecting the catalog): `9101` (MatCMS and Feusys.Web),
  `9262` (TabSale and TapSale), `4433` (Matcad and the old Matcat), `80`/`443` (Matcad and the Caddy inside the MatOS stack).
  Mattiverse picks a free port from its pool when the preferred one is taken and warns about the fixed ones.
- **MatOS** and Mattiverse overlap in purpose (see [ARCHITECTURE.md](ARCHITECTURE.md)); they can run side by side because
  neither needs the other's ports or networks (MatOS uses the network `matos`).
