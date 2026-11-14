# HassSharp

HassSharp is a Home Assistant custom integration for building automations as
graphs. It runs .NET inside the Home Assistant process through Python.NET: each
saved graph is turned into C#, compiled with Roslyn and loaded without restarting
Home Assistant.

Guides: [Writing visual automations](docs/writing-visual-automations.md) ·
[Custom node libraries](docs/node-libraries.md) · [MCP authoring](docs/mcp-authoring.md) ·
[Scheduling](docs/scheduling.md)

## The panel

Open **HassSharp** in the sidebar. It has two pages:

- **Automations** shows a card for each saved automation: a thumbnail of its
  graph, what starts it, its recent runs and whether it has problems. Search,
  filter (needs attention, ran today, never ran, off) and sort the cards. Open a
  card to edit the automation. Each card has an on/off switch: an automation
  that is off is started by none of its triggers, but **Run now** still runs it.
  A card's menu renames the automation, duplicates it (the copy starts off),
  shows its runs, or deletes it. **New automation** asks for a name and opens
  the node editor on a new draft.
- **Node library** holds your own C# files of reusable nodes.

## The node editor

An automation is a graph of steps. Drag steps from the palette onto the canvas,
set them up in the inspector, and connect them: control edges decide what runs
next, typed data edges carry values between steps. Triggers start a run: manual,
startup, entity changes, daily or once schedules, or any native Home Assistant
trigger. Other steps read entities, call services, branch, loop, wait and log.

**Save** validates the graph, compiles it, stores it and activates it. Errors
block the save and point at the step and field they belong to. **Run now** runs
the saved revision from a trigger that allows manual runs. The **Runs** tab lists
every run, newest first; each one opens a trace showing the steps that ran, the
path taken, timings, and each step's input and output values. See
[Writing visual automations](docs/writing-visual-automations.md) for the graph
model, triggers, service calls and run history.

## Where C# fits

There are two places for your own C#:

- **Node libraries.** Mark a public static method with `[Node("Title")]` and it
  becomes a typed step in the palette: parameters are inputs, the return value is
  its result. The library editor has Roslyn completion, diagnostics, hover,
  signature help, formatting and go-to-definition. See
  [Custom node libraries](docs/node-libraries.md).
- **The Custom C# node.** One `[Node]` method of your own inside a graph, for
  one-off logic. **Edit code** opens it in a full-screen editor with completion,
  hover and diagnostics, and its parameters and return value become the node's
  pins; see
  [Writing visual automations](docs/writing-visual-automations.md#c-nodes).

Custom code is trusted administrator code and runs inside Home Assistant.

## Authoring through MCP

HassSharp registers an LLM API with Home Assistant. Home Assistant's built-in MCP
Server serves it to MCP clients, and any conversation agent an administrator
configures with it gets the same tools. An administrator's client or agent can
discover entities and nodes, preview, save and run graphs, turn automations on
and off, edit node libraries and read run history and traces, through the same
revision-checked services as the panel. See [MCP authoring](docs/mcp-authoring.md).

### Security

- The API is available to every conversation agent and MCP client an
  administrator selects it for, not only the MCP Server, and only administrators
  can use it.
- Saving an automation or a node library compiles C# and runs it inside Home
  Assistant's process, with its privileges. Treat the tools that change things as
  code execution. For an agent that must not change anything, use an MCP Server
  entry with **Read only** enabled.
- What the tools return goes to the model's provider: discovered entities
  (whether or not they are exposed to assistants, with only a few attributes),
  service descriptions, graphs and their C#, and run errors and trace values.
- Trace values under secret-looking names are redacted, by name only: this is a
  safeguard, not a security boundary.

See [MCP authoring](docs/mcp-authoring.md#security).

## Installation

On Home Assistant 2026.11 or newer:

1. Open **Settings → Marketplace → Add from link**.
2. Enter `https://github.com/dompagoj/hass-sharp`, connect GitHub if prompted, and
   select **Add**. If asked for a type, choose **Integration**.
3. Open **HassSharp** in the Marketplace and install a release.

HassSharp is a custom integration. Adding this URL under **Settings → Apps**
reports “not a valid app repository”; use the Marketplace steps above. On older
Home Assistant versions, add this URL in HACS as a custom repository with type
**Integration**. For a manual installation, extract a release's `hass_sharp.zip`
into your configuration directory's `custom_components/hass_sharp` folder. Release
packages include the compiled .NET library and frontend.

Restart Home Assistant, add **HassSharp** under **Settings → Devices & services →
Add integration**, and open **HassSharp** in the sidebar. The first start downloads
the .NET 10 SDK into the integration directory, so it needs internet access and
takes longer than later starts. If the download fails, the log says so; restart
Home Assistant to try again.

Automations and node libraries are stored in Home Assistant's
`.storage/hass_sharp/` directory. Earlier versions also loaded hand-written `.cs`
automations from the integration's `user_scripts/` folder. Those are no longer
loaded: if the folder still holds `.cs` files, HassSharp logs one line saying so
and leaves the files as they are.

## Building from source

The repository contains the Python integration, a .NET 10 library and a
SolidJS/Monaco frontend, all in `custom_components/hass_sharp/`. Building a source
checkout requires the .NET 10 SDK and Bun. From the repository root:

```sh
dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out
cd custom_components/hass_sharp/frontend
bun install
bun run build
```

The library is published to `out/` and the frontend builds into `www/`, both
inside the integration directory. For a local Home Assistant instance, use the
repository's `docker-compose.yaml`; see
[the development guide](AGENTS.md#run-home-assistant-locally) for startup,
logging and reload commands.
