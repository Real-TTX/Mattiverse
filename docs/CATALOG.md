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
  role: app                   # app | proxy | ddns | docker  (the wizard's three questions use the last three)
  icon: wiki.svg              # a file below wwwroot/img/apps/, an https URL, or leave out for a generated tile
  homepage: https://example.com/wiki
  recommended: false          # offered first in the wizard (infrastructure roles only)
  ui:
    service: wiki             # the compose service with the web UI
    port: 3000                # its port inside the container
    hostPort: 3000            # tried first on the host; a free port from the pool is used when taken
    path: /login              # optional: appended when the app is opened
  shared: [proxy]             # more services that must be reachable by the reverse proxy (the UI service always is)
  provision: matcad           # optional: a built-in provisioner to run after the install
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
  generated override. Ports that are fixed on purpose (80/443 of a proxy, the mail ports of MatMail) are written in the
  compose file; Mattiverse checks them before the install and warns when another container holds them.
- **Named volumes only.** A relative bind mount would resolve inside the Mattiverse container.
- **Answers reach compose as `${KEY}`** from the app's `.env`. Besides your variables you can use `${MV_SLUG}`,
  `${MV_PROJECT}`, `${MV_PUBLIC_HOST}` and `${TZ}`. Values must not contain a single quote or a line break.
- Every `${KEY}` in the compose file needs a variable or a default (`${KEY:-default}`); the unit tests check this for the
  built-in entries.
- A catalog entry with `provision: matcad` triggers the Matcad set-up after the install; other provisioners are C# classes
  implementing `IAppProvisioner`.

## What Mattiverse adds

For every service of an app the generated `docker-compose.override.yml` sets:

| | |
|---|---|
| `container_name` | `mattiverse-<app>-<service>`: a stable name on the shared network |
| labels | `mattiverse.managed`, `mattiverse.app`, `mattiverse.instance`, `mattiverse.service` |
| networks | `default` plus the shared `mattiverse` network, for the UI service and the services listed in `shared` |
| `ports` | `<host port>:<ui.port>` for the UI service |
