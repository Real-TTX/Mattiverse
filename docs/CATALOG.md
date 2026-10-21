# The app catalog

An app in the store is a normal **`docker compose` file** with one extra top-level block, `x-mattiverse`. Compose ignores
`x-*` keys, so the same file also runs with plain `docker compose up`.

Built-in entries live in [`src/Mattiverse.Core/Catalog/apps`](../src/Mattiverse.Core/Catalog/apps) and are embedded in the
image. Your own entries go to **`/data/catalog/*.yml`** (the data volume); use **Store → Reload catalog** to pick them up.
A file with the same `id` as a built-in entry **replaces** it, and a file that cannot be parsed is skipped and logged.

## Example

```yaml
x-mattiverse:
  id: wiki                    # lowercase letters, digits, dashes; the folder and project name derive from it
  name: Wiki
  tagline: Notes for the family
  description: A longer text for the install page.
  category: Productivity      # groups the store
  role: app                   # app | proxy | ddns | docker | identity | mail  (the role of an engine: Matcad, Matddns, MatDock, MatIdentity, MatSMTP)
  icon: wiki.svg              # a file below wwwroot/img/apps/, an https URL, or leave out for a generated tile
  homepage: https://example.com/wiki
  recommended: false          # offered first in the store (infrastructure roles only)
  core: false                 # true for the five engines: installed by the wizard, cannot be uninstalled
  ui:
    service: wiki             # the compose service with the web UI
    port: 3000                # its port inside the container
    hostPort: 3000            # tried first on the host; a free port from the pool is used when taken
    path: /login              # optional: appended when the app is opened
  shared: [proxy]             # more services that must be reachable by the reverse proxy (the UI service always is)
  provision: matcad           # optional: a built-in provisioner to run after the install (matdock | matcad | matddns | matidentity | matsmtp)
  platform: true              # optional: the app speaks the platform contract (MatDesign docs/PLATFORM.md): Mattiverse gives it MAT_API_KEY and a Manage page
  identity: {}                # optional: the app signs in through MatIdentity (callback /signin-oidc, signedOut /signout-callback-oidc and backchannel /signout-backchannel can be changed)
  mail: {}                    # optional: the app sends mail, through MatSMTP when it is installed: Mattiverse fills MATSMTP_* (sender: the part before the @, default the slug)
  notes:
    - Shown as a hint on the install page.
  variables:
    - key: ADMIN_PASSWORD
      label: Admin password
      type: secret            # text | secret | number
      generate: true          # an empty answer becomes a random password
      help: Leave empty to generate one.
    - key: DB_PASSWORD
      type: secret
      generate: true
      hidden: true            # not asked: always generated
    - key: TIMEZONE
      default: Europe/Berlin
      advanced: true          # below "Advanced" in the form
  credentials:                # the "Initial credentials" card of the app (admins only)
    - label: Username
      value: admin
    - label: Password
      value: ${ADMIN_PASSWORD}
      secret: true            # hidden until the admin asks to see it

services:
  wiki:
    image: ghcr.io/example/wiki:latest
    restart: unless-stopped
    environment:
      ADMIN_PASSWORD: ${ADMIN_PASSWORD}
      TZ: ${TIMEZONE:-UTC}
    volumes:
      - wiki-data:/data

volumes:
  wiki-data:
```

## Rules

- **Do not publish the UI port in the compose file.** Mattiverse publishes it itself (`ui.port` on a free host port) in the
  rendered compose file. Ports that are fixed on purpose (80/443 of a proxy, the mail ports of MatMail) are written in the
  compose file; Mattiverse checks them before the install and warns when another container holds them.
- **Named volumes only.** A relative bind mount would resolve inside the Mattiverse container.
- **Answers reach compose as `${KEY}`** from the app's `.env`. Besides your variables you can use `${MV_SLUG}`,
  `${MV_PROJECT}`, `${MV_PUBLIC_HOST}` and `${TZ}`. Values must not contain a single quote or a line break.
- Every `${KEY}` in the compose file needs a variable or a default (`${KEY:-default}`); the unit tests check this for the
  built-in entries.
- A catalog entry with `provision: matcad` (or `matddns`, `matdock`, `matidentity`, `matsmtp`) connects the engine after the install; other provisioners are C# classes
  implementing `IAppProvisioner`. An engine also needs its API key as a generated, hidden variable (`MATCAD_API_KEY`, `MATDDNS_API_KEY`,
  `MATDOCK_API_KEY`, `MATSMTP_API_KEY`; MatIdentity uses `MAT_API_KEY`) that the compose file passes to the container the way the engine expects it (see [ENGINES.md](ENGINES.md)).
  The five engines are `core: true` (the wizard installs them, the app list cannot remove them); `EngineIds.All` names them in the order they are installed.

## The platform contract and MatIdentity

- `platform: true` adds the hidden, generated variable `MAT_API_KEY`; the compose file passes it as `MAT_API_KEY: ${MAT_API_KEY}`. The app gets a tab **Manage**, drawn from its `/api`.
- `identity: {}` adds the hidden variables `MATIDENTITY_AUTHORITY`, `MATIDENTITY_INTERNAL_URL`, `MATIDENTITY_CLIENT_ID` and `MATIDENTITY_CLIENT_SECRET` (pass them as `${MATIDENTITY_AUTHORITY:-}` ...).
  When MatIdentity is installed Mattiverse registers the app there and fills them on every install and update; without MatIdentity they are empty and the app signs people in itself.
  The registration names the back-channel logout of the app (`backchannel`, default `/signout-backchannel`) at its address on the shared network, so MatIdentity can end the
  app's sessions when a person signs out there (MatPlatform 1.4.0 answers it).
- `mail: {}` (an app that sends mail) adds the hidden variables `MATSMTP_HOST`, `MATSMTP_PORT`, `MATSMTP_USERNAME`, `MATSMTP_PASSWORD` (a secret), `MATSMTP_FROM` and
  `MATSMTP_TLS` (pass them as `${MATSMTP_HOST:-}` ..., to the service that sends the mail; MatDesign docs/PLATFORM.md "Mail through MatSMTP"). When MatSMTP is installed Mattiverse
  makes a relay user for the app there (id = the slug, `managedBy: mattiverse`) and fills them on every install and update: the host is MatSMTP's container on the shared
  network, port and TLS mode come from its status, the sender is `<slug>@<domain>` (the domain of MatSMTP's settings, else the one of the wizard). Without MatSMTP they are
  empty and the app sends its mail itself. `mail: { sender: noreply }` changes the part before the `@`. Needs neither `ui` nor `platform`. The service that sends the mail must
  be on the shared network (the UI service always is, others through `shared`).
- An entry may declare one of these variables itself; then its own declaration is kept.

## What Mattiverse adds

Mattiverse renders the entry into one compose file (`ComposeRenderer`). For every service of an app it sets:

| | |
|---|---|
| `container_name` | `mattiverse-<app>-<service>`: a stable name on the shared network |
| labels | `mattiverse.managed`, `mattiverse.app`, `mattiverse.instance`, `mattiverse.service` |
| networks | `default` plus the shared `mattiverse` network, for the UI service and the services listed in `shared` |
| `ports` | `<host port>:<ui.port>` for the UI service |
| `x-matdock` | one block for the stack (name, description, category), so MatDock's launchpad shows it as an app |
