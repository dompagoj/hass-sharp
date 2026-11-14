# Working on HassSharp

HassSharp embeds .NET in Home Assistant through Python.NET. The Python integration,
C# library, and SolidJS/Monaco frontend live in `custom_components/hass_sharp/`.

## Tests and verification

Agents may add or update tests without waiting for an explicit request. Add useful
regression coverage for bug fixes and behavior changes, and run checks appropriate
to the change. Report what was verified and any checks that could not run.

The suites, from the repository root (`.github/workflows/tests.yaml` runs all of
them on every push and pull request, and a release only after they pass):

```sh
python3 -m unittest discover -s tests/python   # integration; needs Home Assistant installed to skip nothing
python3 -m unittest discover -s tests          # release packaging
dotnet run --project tests/Runtime --disable-build-servers
dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests
(cd custom_components/hass_sharp/frontend && bun run test)
```

Without Home Assistant on the host, the MCP adapter and native trigger tests skip.
To run them against the local container's Home Assistant, copy `tests/python` and
`tests/fixtures` into a temporary directory in the container, link
`custom_components/hass_sharp` there to `/config/custom_components/hass_sharp`, and
run the suite from that directory with `HASS_SHARP_TEST_ROOT` set to it; remove the
directory afterwards.

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
- Saving a visual automation or a node library in the panel recompiles and replaces
  the affected automations without restarting Home Assistant. Both are stored under
  `/config/.storage/hass_sharp/` inside the container, which is not bind-mounted. For
  direct file edits there, reload the integration from **Settings → Devices & services**.
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

**Leak probe.** To log what the process holds after each setup of the entry (the
process's resident memory, .NET heap and assemblies, and the Python object types that
grew), also set `custom_components.HassSharp.memory: debug`. It is off unless that
logger itself is set to debug: ordinary debug logging does not turn it on, because
it walks every Python object and forces a full .NET collection, which stalls Home
Assistant for a moment on each setup and reload.
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

## SQLite packaging regression check

Keep the RID-neutral publish output's `runtimes/` directory in releases; it contains
separate SQLite binaries for glibc/musl and x64/ARM64/ARM. Python.NET does not apply
HassSharp's deps.json native probing paths, so `SqliteNative` resolves the matching
runtime asset before history opens its first connection. Do not flatten one host's
native binary into a multi-architecture release.

After publishing, run with Python.NET installed (the release workflow does this):

```sh
python tests/packaging/sqlite_smoke.py custom_components/hass_sharp
# Against the existing local Alpine HA container, without touching its database:
docker exec -i hass python - /config/custom_components/hass_sharp /config/custom_components/hass_sharp/dotnet < tests/packaging/sqlite_smoke.py
```

This checks Linux ELF architectures and uses a temporary database to verify actual
Python.NET native loading, migrations, persistence, and schedule deduplication.
Pass the .NET root as the optional second argument when it is not discoverable.
