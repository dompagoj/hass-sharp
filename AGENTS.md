# Working on HassSharp

HassSharp embeds .NET in Home Assistant through Python.NET. The Python integration,
C# library, and SolidJS/Monaco frontend live in `custom_components/hass_sharp/`.

## Tests and verification

Agents may add or update tests without waiting for an explicit request. Add useful
regression coverage for bug fixes and behavior changes, and run checks appropriate
to the change. Report what was verified and any checks that could not run.

## Run Home Assistant locally

Run commands from the repository root. Use `docker-compose.yaml` here; the former
`../hass_dev` directory is no longer used. Prerequisites are a running Docker engine
with Compose v2, the .NET 10 SDK, and Bun.

Build the integration before starting a source checkout:

```sh
dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out
(cd custom_components/hass_sharp/frontend && bun install && bun run build)
docker compose up -d
docker compose logs --tail=200 -f hass
```

Open <http://localhost:5001> (container port 8123). Complete onboarding if this is a
new instance, then add **HassSharp** under **Settings → Devices & services → Add
integration** and open its sidebar panel. First integration startup downloads the
Linux .NET SDK into `custom_components/hass_sharp/dotnet/` and installs the Python
requirements, so allow time and internet access. The host SDK builds the library;
the downloaded SDK runs inside the container. Do not substitute a macOS SDK for it.

The integration directory is bind-mounted into
`/config/custom_components/hass_sharp`. Source edits and build outputs are visible
immediately in the container. The Compose project name stays `hass_dev` and the
container name stays `hass`, so these commands also manage the existing instance.

## Debug and reload

- After Python changes, run `docker compose restart hass` and inspect the logs.
- After C# library changes, publish again with the command above, then restart
  `hass` so Python.NET loads the new assembly.
- After frontend changes, run `(cd custom_components/hass_sharp/frontend && bun run build)`
  and hard-refresh the HassSharp panel. For continuous builds, use
  `(cd custom_components/hass_sharp/frontend && bun run build:watch)`.
- Saving a C# automation in the panel recompiles and replaces that script without
  restarting Home Assistant. Scripts live in
  `custom_components/hass_sharp/user_scripts/` on the host. For direct file edits,
  reload the integration from **Settings → Devices & services**.
- Use the browser console and Network/WebSocket tools for editor problems. Check
  `docker compose logs --tail=200 hass` for Python, runtime initialization, compile,
  and automation errors; C# `Logger` messages are forwarded to Home Assistant.

For more detail, merge this logger setting into `/config/configuration.yaml`
inside the container (preserve other settings), then restart Home Assistant:

```yaml
logger:
  default: info
  logs:
    custom_components.HassSharp: debug
```

The logger name is case-sensitive; it is defined in the integration's `const.py`.
To edit the configuration on the host, copy it out and back:

```sh
docker compose cp hass:/config/configuration.yaml /tmp/hass-sharp-configuration.yaml
# Edit /tmp/hass-sharp-configuration.yaml, merging the logger settings above.
docker compose cp /tmp/hass-sharp-configuration.yaml hass:/config/configuration.yaml
docker compose restart hass
```

Stop with `docker compose stop`; resume with `docker compose start`. Only the
integration directory is mounted: other `/config` data is in the container's
writable layer. Before removing or recreating the container (including `down`,
image updates, or `up --force-recreate`), back up `/config` with
`docker compose cp hass:/config /tmp/hass-sharp-config-backup` to a fresh destination.
That data includes onboarding, users, and integration configuration; keep backups
out of version control.
