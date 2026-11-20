# Custom entities

An **entity definition** creates entities in Home Assistant and decides what they do.
Each entity is one of ten types: a binary sensor, a sensor, a switch, a button, a
number, a select, a text, a light, a cover or a camera. A definition can group its
entities into devices, each with a device page of its own (see [Devices](#devices)).
What the entities do is a graph of the same kind as an automation's (graph version
5), built on the canvas from nodes:

- **Steps.** One **Entity** node for each entity, and a few steps only a definition
  has: Set state, When started and the rest below. Everything in
  [Writing visual automations](writing-visual-automations.md) about wires, types,
  value steps and Custom C# steps holds here too.
- **C# code.** A **C# entity** node is one entity written as a C# class that derives
  from an entity class such as `SharpSwitch`; a **C# device** node is one device
  written as a class that derives from `SharpDevice` and adds its entities. Their
  settings are inputs on the node, which take a value or a Setting. See
  [C# nodes](#c-nodes).

One definition can hold Entity nodes, Device nodes, C# entities and C# devices
together. A class in a [node library](node-libraries.md) can still be used as a
whole definition for now; see [A class as the whole definition](#a-class-as-the-whole-definition).

Definitions live in the panel's **Entities** section, beside Automations and Node
library. An assistant can build them too; see [Through MCP](#through-mcp).
Definitions are stored in `.storage/hass_sharp/entities/`, one file each, as
document version 3. A definition built with steps by an earlier development build
(document version 1 or 2) does not load. The **Entities** page lists it apart from the
cards, under **Cannot be read**, by its id with "saved by an earlier development
build; delete it and build it again" and a **Delete** button; it cannot be opened
or turned on. A file that cannot be read for another reason is listed there the same
way, with what was wrong. Until you delete it, the entities it made stay in Home
Assistant as unavailable, and saving a node library is refused with `Cannot read
entity definition …` and the same reason. Delete it on the **Entities** page, then
build it again. A class definition of version 1 or 2 loads as it is. The programming interface is in
[the entity definitions API](backend/entity-definitions.md).

## Build a derived sensor in the panel

Each entity is an **Entity** node on the canvas. Its state values are inputs on its
left: connect a value to each, and Home Assistant shows the state they work out. Its
commands are pins on its right, such as **Turn on** and **Turn off** for a switch:
the steps you connect to one run when Home Assistant sends that command.

This definition makes one binary sensor, **Any motion**, that is on while either of
two motion sensors is on. It needs no trigger: its state is worked out again whenever
either sensor changes. Use two binary sensors of your own where these steps name
`binary_sensor.hallway_motion` and `binary_sensor.kitchen_motion`.

1. Open **HassSharp** in the sidebar, choose **Entities**, then **New entity
   definition**.
2. Enter the **Entity definition name** `Motion in either room`, choose **Build with
   nodes** under **How it works**, and select **Continue**. The editor opens with one
   Entity node on the canvas: a binary sensor named after the definition.
3. Select the Entity node. The panel on the right shows its name at the top and its
   settings below it. Change the name to `Any motion`, set **Key** to `any_motion`
   and **Device class** to `motion`, and leave **Type** as Binary sensor. A text
   field takes its change when you leave it or press Enter.
4. In the **Steps** library on the left, search for `Has state` and drag it onto the
   canvas. In its **Entity** field, pick
   `binary_sensor.hallway_motion`. Its **State** is already `on`.
5. Add a second **Has state** step and pick `binary_sensor.kitchen_motion`.
6. Add an **Or** step (under **Logic**).
7. Wire the values: drag from the first Has state's **Result** to Or's **First
   value**, from the second Has state's **Result** to Or's **Second value**, and from
   Or's **Result** to **On** on the left of the Any motion node.
8. Select **Save**. Once it is saved, the Entity node's panel shows the id Home
   Assistant gave the entity, `binary_sensor.any_motion`, with a **Show** button that
   opens it in Home Assistant. Its key can no longer change.

As a document, the definition you built is this one, apart from the ids and
positions the panel chooses and the settings the panel writes out on the Entity node
as they are by default (`"enabledByDefault": true`, and empty `options` and
`history`). The **On** that the wire fills is `{"kind": "unset"}` under the node's
`arguments`. It is the example "Motion in either room" in
[`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json):

<!-- example: Motion in either room -->
```json
{
  "name": "Motion in either room",
  "settings": [],
  "logic": {
    "kind": "graph",
    "graph": {
      "version": 5,
      "name": "Motion in either room",
      "nodes": [
        {"id": "hallway", "kind": "entity_is", "config": {
          "entity": {"kind": "literal", "type": "entity_id", "value": "binary_sensor.hallway_motion"},
          "state": {"kind": "literal", "type": "string", "value": "on"}}, "position": {"x": 60, "y": 100}},
        {"id": "kitchen", "kind": "entity_is", "config": {
          "entity": {"kind": "literal", "type": "entity_id", "value": "binary_sensor.kitchen_motion"},
          "state": {"kind": "literal", "type": "string", "value": "on"}}, "position": {"x": 60, "y": 280}},
        {"id": "either", "kind": "or", "config": {"left": {"kind": "unset"}, "right": {"kind": "unset"}}, "position": {"x": 360, "y": 100}},
        {"id": "any_motion", "kind": "entity", "config": {
          "key": "any_motion", "domain": "binary_sensor", "name": "Any motion", "deviceClass": "motion",
          "arguments": {"is_on": {"kind": "unset"}}}, "position": {"x": 660, "y": 100}}
      ],
      "edges": [
        {"id": "hallway.value:either.left", "kind": "data", "source": "hallway", "target": "either", "sourcePort": "value", "targetPort": "left"},
        {"id": "kitchen.value:either.right", "kind": "data", "source": "kitchen", "target": "either", "sourcePort": "value", "targetPort": "right"},
        {"id": "either.value:any_motion.is_on", "kind": "data", "source": "either", "target": "any_motion", "sourcePort": "value", "targetPort": "is_on"}
      ]
    }
  }
}
```

The other example definitions in that file are *Plug over HTTP* (a setting, a
Trigger that polls a plug every 10 seconds, steps on the relay's **Turn on** and
**Turn off** that switch it, and a Device node that puts the relay and the power
sensor on one device), *Camera with a box* (the camera steps), *File at setup*
(a download when the definition starts; its address and checksum are placeholders),
and two written in C# (see [C# nodes](#c-nodes)): *Plug from a class*, the same plug
as one C# device node, and *Thermometer over HTTP*, a sensor as one C# entity node.

## Entity types

Each type's row says what its state takes (one input on the left of its Entity node
per value; Set state takes the same), the commands Home Assistant sends it (one pin
each on the right of its Entity node, with the values each gives under it), the
options its Entity node shows (each left empty takes the default shown), and its
entity class, which the class of a [C# entity](#c-nodes) derives from.

| Type | State inputs | Commands (and their values) | Options | Class |
|---|---|---|---|---|
| Binary sensor | **On**: true or false | none | none | `SharpBinarySensor` |
| Sensor | **Value**: a number or a text | none | **Unit**; **State class**: measurement, total or total increasing; **Options**, one per line, for a sensor of the `enum` device class | `SharpSensor` |
| Switch | **On** | Turn on, Turn off | none | `SharpSwitch` |
| Button | nothing: a button has no state | Press | none | `SharpButton` |
| Number | **Value**: a number within its range | Set value (**Value**) | **Minimum** (0), **Maximum** (100), **Step** (1, above 0), **Shown as**: auto, box or slider; **Unit** | `SharpNumber` |
| Select | **Option**: one of its options | Select option (**Option**) | **Options**, one per line; required | `SharpSelect` |
| Text | **Text** | Set text (**Text**) | **Shortest length** (0), **Longest length** (100, at most 255), **Pattern**: a regular expression matched from the start of the text (end it with `$` to match the whole text); **Shown as**: text or password | `SharpText` |
| Light | **On**; **Brightness**, a whole number from 0 to 255, optional, only with **Can be dimmed** | Turn on (**Brightness**, optional), Turn off | **Can be dimmed** | `SharpLight` |
| Cover | **Closed**, optional; **Position**, a whole number from 0 (closed) to 100 (open), optional, only with **Has a position** | Open, Close, Stop, Set position (**Position**) | **Has a position** | `SharpCover` |
| Camera | nothing: its picture comes from Show frame | none; **When a frame is needed** instead (see [Cameras](#cameras)) | **Seconds between frames**: 0.5 to 60 (1) | `SharpCamera` |

Select an Entity node to edit it in the panel on the right: its name at the top
(what Home Assistant shows), then its **Key**, its **Type**, a **Device class** (a
Home Assistant device class, such as `motion` or `power`), an **Icon** (such as
`mdi:motion-sensor`), a **Category** (None, Config or Diagnostic), **Enabled by
default**, the options of its type and its **Run history** (see
[Run history](#run-history)). A required option left empty, such as a select's
**Options**, says it is missing on its field, and **Save** waits for it.

With no step selected, the panel on the right holds the definition's **Settings**,
with any problem of the definition as a whole above them; its devices are its Device
nodes (see [Devices](#devices)). On a phone, tap **Settings** at the bottom to open it. A problem of
an entity is the Entity node's: choosing it in the **Problems** tab selects the node
and puts the cursor in the field it is about.

A sensor with options takes only one of them. With a unit, a state class or a device
class other than date, timestamp and enum, it takes a number or a text that reads as
one. Otherwise it takes any number or text. A value an entity does not take fails
the step that gives it (see [The Entity node](#the-entity-node)).

## The Entity node

Add an **Entity** node from the **Entity** category of the step library for each
entity the definition creates, and choose its **Type** in the panel on the right. A
new definition starts with one, a binary sensor named after the definition.

- **Its state** comes from its inputs on the left, one per value of its type: connect
  a value to an input, or type one in. Once one has a value, every value the type
  needs must have one; a light's **Brightness** is optional, its **On** is not. The
  state is worked out again whenever an entity chosen on an entity input of the
  value steps it reads changes: the entity of a Has state or a Current state step,
  or a Constant of an entity id type. An entity read by another step, such as Read
  entity, is not followed. An input cannot read a step that runs in a flow, such as
  an HTTP request after a Trigger: set the state with a **Set state** step after
  that step instead. A state that fails, or gives a value its entity does not take,
  makes the entity unavailable and logs one warning per reason, naming the entity;
  the entity is available again once its state works out.
- **Its commands** are pins on its right, such as **Turn on** and **Turn off** for a
  switch. The steps connected to a command run when Home Assistant gives the entity
  that command. A command's values, such as a light's **Brightness**, are pins under
  it, and only the steps that run from that command can read them. Two commands can
  lead to the same step. The steps must finish within 30 seconds; a failure, or the
  30 seconds passing, fails the service call that sent the command (a flow past its
  30 seconds goes on in the background). A command with no step connected to it is a
  warning: "Relay does nothing on Turn on: connect a step to Turn on".
- **A camera** has **When a frame is needed** on its right instead; see
  [Cameras](#cameras).
- **Its device.** The **Device** pin in its header takes a wire from a Device
  node's **Entities** pin, which puts the entity on that device; the **Device** field
  in its panel does the same. Without one it stands alone. See [Devices](#devices).
- **Its name.** An entity on no device needs a name: "Name this entity: it has no
  device, so Home Assistant shows it by its own name". One on a device may leave it
  out.

The runs of an Entity node are named after its entity and the pin they start at:
"Any motion: State", "Relay: Turn on", "Front door with a box: When a frame is
needed". A node without a name is named by its key.

## Trigger, When started and the other steps

These are the other steps that start something in a definition, and the steps that
act on its entities. A step names one of the definition's entities by choosing it in
its **Entity** field.

- **Set state** sets an entity's state inside a flow (after a Trigger, When started
  or a command). It sets the values you give it and keeps the others as they are,
  such as a light's brightness when only On is given. **Set available** marks an
  entity unavailable, or available again.
- **Trigger** runs the steps after it when something happens, at a set time or on an
  interval, as in an automation: to poll a device, for example. Its types are those
  of [an automation's triggers](writing-visual-automations.md#triggers-and-their-outputs);
  "Only manually" runs only when started with **Run now** or by an assistant.
- **When started** runs the steps after it once each time the definition starts,
  such as to set its entities' first states or to download a file. Its flow has 60
  seconds, not counting the time a Download file step spends receiving. A When
  started flow that fails keeps the definition from starting.
- **Setting** gives the value of one of the definition's settings (see below).

A **Stop** step ends a run there, and the run is recorded as Stopped, which is not a
failure: a command's run that stops makes its service call succeed, a When started
run that stops lets the definition start, and a Trigger's or a frame's run that
stops is not an error. A state's run that stops does not make its entity
unavailable, and does not clear an earlier failure either.

**Run now**, in the editor's top bar, lists the Trigger steps and starts one by hand;
a Trigger whose **Can be started with Run now** is off is not offered.
It runs the saved definition only: it is unavailable, with the reason as its tooltip,
while there are unsaved changes ("Save the entity definition to run this step"), and
while the definition is off, starting or not running. Unlike an automation, a
definition that is off cannot be run. Nothing else is run by hand: a command runs
when the entity's service is called in Home Assistant (try it on the entity's page),
an Entity node's state follows its sources, When started runs when the definition
starts, and a frame is made when a camera is watched.

## Settings and secrets

A setting is a value the steps read with a **Setting** step, such as an address or a
threshold, so that it can change without editing the steps. Add one under
**Settings** with **Add setting**: it has a **Key**, a **Label**, a **Type** (a text,
a number, or true or false) and a **Value**. A Setting step's output has the setting's
type.

Mark a password or a token as **Secret**. Once saved, its value is never shown again,
in the panel or in any answer the panel or an assistant gets: the field says it is
saved, and you type a new value to replace it, or **Clear** it. A secret's value is
left out of this browser's drafts and of Export graph. Run traces, the Log step and
the service logs show it as hidden, and messages say that a value is hidden instead
of quoting it. Two things can still show it: a true or false worked out from a secret
is recorded as it is, and a step that returns what it was given (a custom step or a
service response) records its output as it is.

A C# node's settings are its class's own `[Setting]` properties, each an input of
the node: type a value into it, or connect a Setting step to it (see
[Settings as inputs](#settings-as-inputs)). A secret one takes only a Setting.

## Devices

A device groups entities on one device page in Home Assistant. Each device is a
**Device** node, from the **Entity** category of the step library (a node of kind
`device`), or a [C# device](#c-nodes) node, whose code adds its own entities; a
definition can have several, or none, and is not a device itself. This section is
about the Device node.

- **Its key** names it inside the definition and is part of its identity in Home
  Assistant: a lower-case letter, then up to 39 lower-case letters, digits or
  underscores, such as `plug`, and one key per device, a C# device's included. Once
  saved it cannot change. Device keys and entity keys are apart: a device and an
  entity may have the same key.
- **Its name**, at the top of its panel, is what Home Assistant shows. A Device node
  needs one: "Name this device: Home Assistant shows it by this name".
- **Its entities.** Connect its **Entities** pin (its `entities` output) to the
  **Device** pin of each Entity node on it (the Entity node's `device` input), or
  choose the device in the Entity node's **Device** field. An Entity node with no
  wire stands alone, on no device, and needs a name of its own. A Device node with no
  entities is a warning. The wire carries no value: it is not part of the entity's
  state, and a Device node is never part of the run order.
- **Manufacturer**, **Model**, **Software version**, **Hardware version**,
  **Configuration URL** (`http://`, `https://` or `homeassistant://` followed by an
  address, such as `http://192.168.1.20`; a few addresses Home Assistant would take,
  such as one with a user name before the host, are refused here) and **Suggested area** (the area Home Assistant puts the device
  in when it first adds it) are shown on the device's page. Each can be left out.
- **Part of** says which device this one is connected through, such as a hub:
  Nothing, **Another device of this definition** (another Device node or a C# device,
  chosen by its name) or **A Home Assistant device…**, which offers the devices Home
  Assistant has. A device part of a key no device of the definition has, or a circle of devices
  each part of the next, is a problem on the Device node. When the device is set up, a
  Home Assistant device that is gone is left out, and clears an earlier link.

As a document, a Device node's `config` is
<!-- config: device -->
`{key, name, manufacturer?, model?, swVersion?, hwVersion?, configurationUrl?,
suggestedArea?, via?}`, a `?` marking what can be left out; `via` is
`{"deviceId": "<Home Assistant device id>"}` or `{"device": "<the key of another
device of the definition>"}`. The wire is a data edge from the
Device node's `entities` output to the Entity node's `device` input. In *Plug over
HTTP* the relay and the power sensor are on the device `plug`:

<!-- example: Plug over HTTP, its device -->
```json
{
  "nodes": [
    {"id": "plug", "kind": "device", "config": {"key": "plug", "name": "Plug over HTTP", "model": "Smart plug", "suggestedArea": "Living room"}, "position": {"x": 60, "y": 820}}
  ],
  "edges": [
    {"id": "plug.entities:relay.device", "kind": "data", "source": "plug", "target": "relay", "sourcePort": "entities", "targetPort": "device"},
    {"id": "plug.entities:power.device", "kind": "data", "source": "plug", "target": "power", "sourcePort": "entities", "targetPort": "device"}
  ]
}
```

A saved definition lists its devices under `devices`, its Device nodes' and then
its C# devices', and gives each of its entities the key of the device it is on, or
null, under `entities`, its Entity nodes' and then its C# entities'; both are worked
out from the graph, for reading only. The entities a C# device's code adds are not
listed there: Home Assistant has them once it has started. Home Assistant knows a
device by the definition's id and the device's key, and an entity by the
definition's id and the entity's key, so an entity moved to another device, or off
its device, keeps its entity id and its history.

Home Assistant makes the entity ids. On a device, an entity's id comes from the
device's name and the entity's name; an entity without a name takes the device's
name alone. Without a device, the id comes from the entity's name alone, which is
why such an Entity node needs one (a C# entity on no device whose code gives it no
name is called its class's title; a class definition's entity without one takes the
definition's name); name it with clashes in mind: the derived sensor above is
`binary_sensor.any_motion`.

## HTTP, downloads and restored state

**HTTP request** (in automations too) sends a request to a device or a web service
at an `http://` or `https://` address. Its **URL**, **Headers** (an object) and
**Body** are inputs; a text body is sent as it is, an object or a list as JSON, and
a GET or HEAD cannot have a body. Its settings:

- **Method**: GET, POST, PUT, PATCH, DELETE or HEAD.
- **Response type**: Text, JSON object or JSON list. A JSON answer is read with Get
  field; an answer of another shape fails the step.
- **Timeout**: 1 to 300 seconds for the whole answer, 10 unless set.
- **Fail on an error status** (on unless turned off): fail on a status outside 200
  to 299, or go on and give the status.
- **Keep the response out of run history**: show the answer's body and headers as
  hidden in run history, and keep them hidden in the steps that use them, as a
  secret setting is. Turn it on when an answer can hold a password or a token.

Its outputs are **Status**, **Body** and **Headers**. It checks the server's
certificate, takes 10 seconds at most to connect, reads answers of up to 4 MiB and
keeps no cookies. It follows at most 5 redirects: to the same host with its headers;
to another host only when the step has no headers, and otherwise it fails, so the
headers are never sent there. A 303, and a 301 or 302 after a POST, become a GET
without the body; a 307 or 308 keeps the method and the body; a redirect from https
to http is not followed, and is the answer. A failure names the
host only, never the path, the query, a header or the body.

No step joins texts, so steps alone cannot build several addresses from one setting.
Use one setting per address, one address that takes what to do in the body (as both
plug examples do), or a Custom C# step.

**Download file** fetches a file a definition needs, such as a model, from its
**URL**, once: it checks the file against its **SHA-256 checksum**, of the file as the
server sends it, and gives its **Path**. The file is kept under
`.storage/hass_sharp/files/<checksum>/` with its **File name** (the last part of the
address when none is given) and is not downloaded again. A file larger than 2 GiB,
one that does not match its checksum, or a server that sends nothing for 60 seconds
fails the step, and nothing is kept. Download in When started, so the file is there
before the entities run.

Entities whose Entity node works out no state from its inputs come back, after Home
Assistant restarts or the definition is saved, with the state they last had: switches, numbers, selects, texts,
lights and covers set by commands, for example. A value the entity no longer takes,
such as an option removed from a select, is left out. In a graph, a light turned on
without a brightness keeps its last one, or comes on at full brightness the first
time. A C# node's class chooses this per entity with `RestoreState`.

## Cameras

A camera shows the picture its last **Show frame** step gave it. A camera's Entity
node and four steps work with pictures, in definitions only:

- **When a frame is needed**, the pin on the right of a camera's Entity node, runs
  the steps connected to it when Home Assistant needs a new picture from the camera,
  which it does only while someone watches it or asks for its picture: nothing runs
  otherwise.
- **Take a picture** takes the current picture of another Home
  Assistant camera.
- **Draw rectangle** and **Draw text** are value steps that draw on a picture: a
  **Colour** (Red, Green, Blue, Yellow, Orange, White or Black), a rectangle's
  **Thickness** (1 to 50 pixels, 2 unless set), a text's **Size** (8 to 200 pixels,
  16 unless set) and **Background**, a translucent box behind it.
- **Show frame** makes a picture the camera's current one, in a frame
  flow or in any other flow, such as a Trigger's.

So a camera either makes its frames when they are needed (steps on When a frame is
needed that end in Show frame), or is given them by other flows. A frame flow that
ends without Show frame keeps the current picture, and a camera that no Show frame
step names is a warning: "Front door with a box never shows a frame: add a Show
frame step for it". The example "Camera with a box" takes a picture of
`camera.front_door`, draws a box and a caption on it and shows it.
A camera written in C# does the same with `Frame()` or `OnFrame`, and `Show`; see
[Cameras in C#](#cameras-in-c).

While someone watches, Home Assistant asks for a frame every **Seconds between
frames**. Viewers share one frame: a request within 0.8 of an interval of the last
frame begun gets that frame again. Making a frame has 8 seconds; a graph's frame flow
is cancelled then. When making one fails, the last good frame is shown for the longer
of 30 seconds and twice the interval; after that, or when there has never been one,
Home Assistant shows no picture. Failures are logged once per reason, naming the
camera, and once when it works again.

Limits: a picture is a JPEG of at most 20 MiB and 40 megapixels, and carries at most
500 drawings; a text is at most 200 characters. A position or size that is not whole
is rounded to the nearest pixel, and drawing outside the picture is left out, never
an error. A drawn picture is encoded at JPEG quality 80. Pictures are not resized:
Home Assistant scales a still picture itself.

Decoding and drawing need the system's libjpeg-turbo library (`libturbojpeg`).
Home Assistant's container image has it; on 32-bit ARM, on i386 and for Home
Assistant Core in a virtual environment this is unverified. Without it, a picture
passed on unchanged still works, and drawing fails with one log line saying the
library is missing.

## C# nodes

An entity or a device can be written in C# as one class, in a node on the canvas:

- A **C# entity** node is one entity. Its class derives from one of the entity
  classes of the [table](#entity-types), such as `SharpSwitch` or `SharpSensor`, which
  is its type. Like an Entity node it has a **Device** pin: connect a Device node's
  **Entities** to it to put it on that device, or leave it unconnected and it stands
  alone.
- A **C# device** node is one device. Its class derives from `SharpDevice` and adds
  its entities itself, so nothing is connected to it, and it has **Name** and **Part
  of** as a Device node has.

Both are in the **Entity** category of the step library. A C# entity dropped on the
canvas gets the key `sensor` (then `sensor_2` and so on), and a C# device a key made
from `device`; each starts with example code you replace. Neither is a step: they
have no step order, nothing runs them, and the class sets its entities' states and
handles their commands itself. A step cannot set a C# entity's state or start from
its commands.

The node's card shows the class's title. A C# entity's card says its type and key,
such as **Sensor · thermometer**; a C# device's says **C# device · plug** and lists
its entities once it has started, until then **Its entities appear here after it has
started once**. In the right panel are its **Key**, a C# device's **Name** and
**Part of**, **Edit code**, and a field for each setting of its class (see
[Settings as inputs](#settings-as-inputs)).

### Write the code

**Edit code** opens the code in the editor, titled **C# entity code** or **C# device
code**, with completion and the problems of the code as you type. The **Settings**
panel beside it shows the settings the code gives; a class without any says "This
class has no settings. Mark a property with [Setting] to make it an input." **Reset
to example** puts back the example code, and the help explains what a class can do,
as this section does.

The code is one public class, not abstract or generic, with a public constructor
that takes nothing. Helper types can sit beside it and `using` lines go at the top.
Each node's code is compiled on its own, so a type one node declares cannot be named
in another node's code: declare it in both, or in a [node library](node-libraries.md),
which every node's code can use. Its title is its name in words ("Smart plug"), or
the title of a `[Node("…")]` on the class, and its summary comment is its
description.

**Apply** writes the code to the node, once it has been checked and has no errors,
and fits the node to it: a setting the code no longer has loses its pin, a value that
no longer fits its setting is cleared, and a wire into a setting that is gone or of
another type is removed. The editor says what it removed: "Removed 1 connection that
no longer fits: Address" or "Cleared 2 values that no longer fit: Interval, Mode".
Undo puts it all back in one step.

This is the C# device of the example "Plug from a class": a plug with a relay and a
power sensor, polled every 10 seconds, that answers JSON-RPC at the address in its
Address setting.

<!-- example: Plug from a class -->
```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>A smart plug that answers JSON-RPC requests: a switch for its relay and a sensor for the power it draws.</summary>
public class SmartPlug : SharpDevice
{
    /// <summary>The plug's JSON-RPC address.</summary>
    [Setting("Address", Description = "Where the plug answers, such as http://192.168.1.50/rpc.")]
    public string Address { get; set; } = "";

    readonly SharpSwitch relay;
    readonly SharpSensor power;

    /// <summary>Adds the plug's two entities, the switch turning the plug on and off.</summary>
    public SmartPlug()
    {
        relay = Add(new SharpSwitch("relay", "Relay") { DeviceClass = "outlet", OnTurnOn = () => Turn(true), OnTurnOff = () => Turn(false) });
        power = Add(new SharpSensor("power", "Power") { DeviceClass = "power", Unit = "W", StateClass = "measurement" });
    }

    /// <summary>Refuses to start without an address the plug can be reached at.</summary>
    protected override Task Setup()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The plug's address must start with http:// or https://, such as http://192.168.1.50/rpc.");
        return Task.CompletedTask;
    }

    /// <summary>Reads the plug's status every 10 seconds while the definition runs.</summary>
    protected override Task Start()
    {
        Every(TimeSpan.FromSeconds(10), Poll);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the switch and the sensor from the plug's status. While the plug does not answer, both are unavailable, and
    /// Every logs why once.
    /// </summary>
    async Task Poll()
    {
        try
        {
            var status = await Call("Switch.GetStatus", new JsonObject { ["id"] = 0 });
            relay.IsOn = status?["output"]?.GetValue<bool>();
            power.Value = status?["apower"]?.GetValue<double>();
            relay.Available = true;
            power.Available = true;
        }
        catch
        {
            relay.Available = false;
            power.Available = false;
            throw;
        }
    }

    /// <summary>Turns the plug on or off, and the switch with it.</summary>
    async Task Turn(bool on)
    {
        await Call("Switch.Set", new JsonObject { ["id"] = 0, ["on"] = on });
        relay.IsOn = on;
    }

    /// <summary>Sends one request to the plug and returns its result.</summary>
    async Task<JsonNode?> Call(string method, JsonObject parameters)
    {
        var request = new JsonObject { ["id"] = 1, ["method"] = method, ["params"] = parameters };
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(Address, content, Stopping);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Stopping))?["result"];
    }
}
```

In the definition, the device's Address takes the definition's Address setting
through a Setting step, so the address is changed under **Settings** without opening
the code. As stored (the code written as `…`), its nodes and the wire are:

<!-- example: Plug from a class, its nodes -->
```json
{
  "nodes": [
    {"id": "address", "kind": "setting", "config": {"key": "address"}, "position": {"x": 60, "y": 100}},
    {"id": "plug", "kind": "csharp_device", "config": {"key": "plug", "name": "Smart plug", "source": "…", "signature": {"title": "Smart plug", "description": "A smart plug that answers JSON-RPC requests: a switch for its relay and a sensor for the power it draws.", "domain": null, "settings": [{"key": "Address", "label": "Address", "description": "Where the plug answers, such as http://192.168.1.50/rpc.", "type": "string", "secret": false}]}, "arguments": {}}, "position": {"x": 360, "y": 100}}
  ],
  "edges": [
    {"id": "address.value:plug.Address", "kind": "data", "source": "address", "target": "plug", "sourcePort": "value", "targetPort": "Address"}
  ]
}
```

The example "Thermometer over HTTP" is one C# entity, a sensor that reads the
temperature at its Address every Interval seconds. Its Address takes a Setting, as
the plug's does, and its Interval is typed into the node: 60.

<!-- example: Thermometer over HTTP -->
```csharp
using System.Globalization;

/// <summary>A thermometer that answers with the temperature in degrees Celsius at its address.</summary>
public class Thermometer : SharpSensor
{
    /// <summary>Where the thermometer answers, such as http://192.168.1.60/temperature.</summary>
    [Setting("Address")]
    public string Address { get; set; } = "";

    /// <summary>How many seconds pass between two readings.</summary>
    [Setting("Interval", Minimum = 10, Maximum = 3600)]
    public int Interval { get; set; } = 60;

    /// <summary>A temperature in degrees Celsius.</summary>
    public Thermometer()
    {
        DeviceClass = "temperature";
        Unit = "°C";
        StateClass = "measurement";
    }

    /// <summary>Refuses to start without an address the thermometer can be reached at.</summary>
    protected override Task Setup()
    {
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("The thermometer's address must start with http:// or https://, such as http://192.168.1.60/temperature.");
        return Task.CompletedTask;
    }

    /// <summary>Reads the temperature every Interval seconds while the definition runs.</summary>
    protected override Task Start()
    {
        Every(TimeSpan.FromSeconds(Interval), Read);
        return Task.CompletedTask;
    }

    /// <summary>Sets the sensor from the thermometer's answer. While it does not answer, the sensor is unavailable, and Every logs why once.</summary>
    async Task Read()
    {
        try
        {
            Value = double.Parse(await Http.GetStringAsync(Address, Stopping), CultureInfo.InvariantCulture);
            Available = true;
        }
        catch
        {
            Available = false;
            throw;
        }
    }
}
```

As a document, a C# entity node's `config` is
<!-- config: csharp_entity -->
`{key, source, signature?, arguments?}` and a C# device node's is
<!-- config: csharp_device -->
`{key, name, via?, source, signature?, arguments?}`: `source` is the code,
`signature` the settings worked out from it (the panel writes it when you apply the
code), and `arguments` the values typed into its settings. A C# device's `via` is
as a Device node's.

### Settings as inputs

`[Setting("Label")]` on a public property with a public setter makes it a setting:
an input of the node, named by the label, whose pin id is the property's name. It may
be a `string`, `bool`, `int`, `long`, `double` or an enum. `Description` is a line of
help (the property's summary comment, when it has none); `Secret = true` makes it a
secret; `Minimum` and `Maximum` bound an `int`, `long` or `double`. A C# entity's
property cannot be named `device`, the id of its Device pin.

Each setting takes one of these:

- **A value typed into the node**, in its field in the right panel: a text, a number,
  a switch for a `bool`, a list for an enum. A value outside the bounds, or a fraction
  for an `int`, is refused when you save.
- **A wire from a Setting step**, which gives one of the definition's
  [settings](#settings-and-secrets); no other step can feed it. Use this for what
  changes from one home to another, such as an address, so it is changed under
  **Settings**.
- **Nothing**: the property keeps the value the class gives it.

A secret is never typed into the node: its field says "A secret is not typed here:
connect a Setting". Add a secret setting to the definition, a Setting step for it,
and connect the step to the node. The values are applied before `Setup()` runs, and
a Setting step's value is read when the definition starts.

### Keys and names

A C# entity's key is one of the definition's entity keys, beside its Entity nodes',
and a C# device's key one of its device keys, beside its Device nodes'. Like theirs,
a key is a lower-case letter, then up to 39 lower-case letters, digits or
underscores, and cannot change once saved.

- A C# entity is the entity `<definition id>:<key>` in Home Assistant, as an Entity
  node is.
- A C# device is the device `<definition id>:<device key>`, and each entity its code
  adds is `<definition id>:<device key>:<entity key>`, with the key the code gives it
  (`"relay"` above). Two C# devices of the same code are two devices, each with its
  own entities.
- A C# entity is called the `Name` its code sets. Without one, standing alone, it is
  called its class's title; on a device, Home Assistant shows the device's name. A
  C# device is called its **Name**.

### SharpDevice and SharpEntity

- **`SharpDevice`** is a device with several entities that share its code and its
  settings. Add each entity in the constructor (or in `Setup()`) with `Add(entity)`,
  which returns it so it can be kept in a field; after that `Add` throws. Its
  `Manufacturer`, `Model`, `SwVersion`, `HwVersion`, `ConfigurationUrl` and
  `SuggestedArea` are read once, after `Setup()`.
- **`SharpEntity`** is the base of the typed entities. A C# entity's class derives
  from one of them; inside a device, create them with a key of their own: a
  lower-case letter, then up to 39 lower-case letters, digits or underscores. Each
  has `Key`, `Name`, `Icon`, `DeviceClass`, `Category` (`EntityCategory.Config` or
  `Diagnostic`), `EnabledByDefault`, `Available`, `Attributes` (extra attributes
  Home Assistant shows with the state), `RestoreState` and `Publish()`.
- **The typed entities** are the classes of the [table](#entity-types). Setting a
  state property (`IsOn`, `Value`, `Option`, `Brightness`, `IsClosed`, `Position`)
  publishes the new state; null is unknown. A command is handled by overriding its
  method (`TurnOn`, `TurnOff`, `Press`, `SetValue`, `SelectOption`, `Open`, `Close`,
  `StopMoving`, `SetPosition`) or, inside a device, by setting its `On…` property, as
  `OnTurnOn` above. A value outside a number's range, an option a select does not
  have, a text that does not fit, a brightness outside 0 to 255 or a position
  outside 0 to 100 is refused before the handler runs, and a handler that throws
  fails the command with its message. The options are properties too: `Unit`,
  `StateClass`, `Options`, `Min`, `Max`, `Step`, `Mode` (`SharpNumberMode`,
  `SharpTextMode`), `Pattern`, `Dimmable`, `Positionable` and a camera's
  `FrameInterval`.

### Setup, Start and Stop

- **`Setup()`** runs once before the entities are registered, with the settings
  applied: the place to check the settings or fetch what is needed. Creating every
  C# node of the definition and running their `Setup()`s have 60 seconds together,
  not counting the time a download receives. A constructor or a `Setup()` that throws
  or takes longer keeps the definition from starting, and the definition says why,
  naming the node: "Plug from a class: Smart plug: The plug's address must start with
  http:// or https://…". The When started steps run after every `Setup()`, with 60
  seconds of their own.
- **`Start()`** runs after the entities are registered, one C# node's after
  another's, a device's before its entities'. It is the place to
  start timers and tracks. It must return; the entities stay unavailable
  until every `Start()` of the definition has, so poll with `Every`, not in a loop.
- **`Stop()`** runs when the definition stops or is replaced, after its timer and
  track callbacks and commands have ended, node by node, a device's entities before
  the device; it may still call services. It runs only once the definition has
  started, so a definition whose `Setup()` threw gets no `Stop()`: undo what a
  failing `Setup()` opened in `Setup()` itself. It runs on the device and every
  entity, also one whose `Start()` never ran, so it must not count on what `Start()`
  set up. It does not run when Home Assistant shuts down: then only `Stopping` is
  cancelled.
- **`Stopping`** is a cancellation token that is cancelled when the definition stops
  or is replaced, and when Home Assistant shuts down. Pass it to what you wait for.

Each save makes new instances of the classes. Their entities take back the states
they had (see [restored state](#http-downloads-and-restored-state)) unless
`RestoreState` is false.

### Timers, tracks and Home Assistant

- **`Every(interval, action, immediately = true)`** runs `action` every `interval`
  until the definition stops, or the handle it returns is disposed. A run that throws
  is logged as an error once for each new reason, and once when it works again, so a
  device that does not answer is not logged on every tick.
- **`Track(entityId, change => …)`** runs each time an entity changes, with an
  `EntityChange` of its new state (`To`) and its state before (`From`, null when it
  has just appeared).
- **`State(entityId)`** reads an entity's state now; **`CallService(service, target,
  data, returnResponse)`** calls a service, also from `Stop()`; **`Log(message,
  level)`** writes to the Home Assistant log.
- **`CameraSnapshot(entityId)`**, or **`CameraSnapshot(entityId, cancellation)`**
  to stop waiting when a token is cancelled, takes a picture with one of Home
  Assistant's cameras as a `SharpImage`, within 10 seconds.

`Every`, `Track`, `Http` and `Files` work from `Setup()` on (start timers and tracks in
`Start()`); in the constructor they throw.

### Http and Files

- **`Http`** is the definition's own `HttpClient`, the same for all its C# nodes and
  their entities: 30 seconds per request, at most 5 redirects, certificates always
  checked, no cookies and `User-Agent: HassSharp`. Pass `Stopping` to each request.
  It is disposed after the last `Stop()`.
- **`Files.Download(url, sha256, name, cancellationToken)`** downloads a file once
  and returns its path, as the Download file step does (same folder, checksum, limits
  and timeouts): `var path = await Files.Download(url, sha256, cancellationToken:
  Stopping);` in `Setup()`. It throws on a malformed address or checksum, a file that
  does not match, an error status, or 60 seconds without data.

### Cameras in C#

A `SharpCamera` shows a picture with `Show(image)`, from any thread, or makes one
when it is needed by overriding `Frame()` (or, inside a device, setting `OnFrame`),
which returns the picture to show, or null to keep the one shown. `Frame()` has 8
seconds; C# code cannot be cancelled, so one that takes longer is left to finish
and what it gives is not shown, and no second one starts meanwhile. A `SharpImage`
comes from `SharpImage.Load(jpeg)` or `SharpImage.Blank(width, height, color)`,
takes `DrawRect(x, y, width, height, color, thickness)` and `DrawText(text, x, y,
color, size, background)`, each of which returns the same image, and gives its
bytes with `ToJpeg()`. Colours are `SharpColor.Red` (the default), `Green`, `Blue`,
`Yellow`, `Orange`, `White`, `Black` or `SharpColor.FromRgb(r, g, b)`. A picture is
for one flow at a time: hand another thread a `Copy()`.

This is the code of a C# entity that is a camera:

```csharp
/// <summary>The front door's camera with a box drawn on its picture.</summary>
public class BoxedDoor : SharpCamera
{
    /// <summary>A frame every 2 seconds while someone watches.</summary>
    public BoxedDoor() { FrameInterval = 2; }

    /// <summary>Takes the door camera's picture and draws on it.</summary>
    protected override async Task<SharpImage?> Frame()
    {
        var picture = await CameraSnapshot("camera.front_door", Stopping);
        return picture.DrawRect(40, 40, 240, 160, SharpColor.Yellow, 4).DrawText("Front door", 40, 8, SharpColor.White, 24);
    }
}
```

### A class as the whole definition

A class from a node library can still be used as a whole definition for now, but
write new C# as C# nodes. Such a class is public, concrete and not generic, has a
public constructor that takes nothing, and derives from `SharpDevice` or from one of
the typed entities; it is offered when you choose **Entities → New entity
definition**, enter a name, choose **Use a class from the node library** and select
**Continue**. Under **Class**, choose it; under **Settings**, fill in its settings;
select **Save**. Its **Device** fields override what the class sets when you fill
them in, and its **Part of** offers Nothing, each other class definition that makes
a device, or **A Home Assistant device…**. A class that is one entity (its key is
`main`) has **Group this entity as a device** instead. Its **Entities** appear after
it has started once. A secret setting of such a class is typed in and hidden, as a
definition's secret setting is.

## Run history

Each definition lists its runs under **Runs** in the editor's bottom drawer, and
each run opens its trace, as an automation's does; see
[run history and traces](writing-visual-automations.md#run-history-and-traces). An
Entity node's **Run history** has a switch for each kind of run it starts: **Record
commands** is on unless turned off, and **Record state updates** and **Record
frames** are off unless turned on. Each other step's **Track in run history**
decides for its runs: on unless turned off for When started, off unless turned on
for a Trigger (unlike in an automation, where a Trigger is recorded unless turned
off). A run started with Run now, or by an assistant, is always recorded. What a C#
node's code does is not recorded: it has no steps to trace, and neither has a class
definition.

## Worth knowing

### Keys and ids

- An entity's key is permanent once saved: with the definition's id it is the
  entity's identity in Home Assistant. The server does not refuse a changed key:
  changing it (through an assistant, or in the stored document) makes a new entity,
  and Home Assistant may give the new one an id ending in `_2` while the old
  registry entry still exists. Before the first save, changing a key changes it in
  every step that uses the entity.
- A copied or duplicated Entity node gets a key of its own, and the steps copied
  with it follow that key.
- Deleting an Entity node that steps use asks first: those steps show a problem
  until you change or remove them.
- A deleted entity whose unique id comes back, such as a definition deleted and made
  again with the same id and key, gets back the customisation Home Assistant kept for
  it in its registry.
- `new` is never a definition's id.
- An automation and a definition may have the same id; each stays apart from the
  other.

### Saving and replacing

- Saving creates or updates the entities at once. A save answers that the definition
  is running up to about a quarter of a second before Home Assistant shows the
  entities as available, and a service call in that window does nothing.
- While a save replaces a running definition, a command answers "The entity is not
  running".
- Each card on the **Entities** page, and the editor's top bar, has an on/off
  switch. A definition that is on starts again whenever Home Assistant starts. A card
  says how many devices its definition makes: **Device** for one, **2 devices** and so
  on for more, nothing for none.
- A definition built with steps is not a device itself: its devices are its Device
  nodes and C# devices. One sent with a device, through MCP or the panel's API, is refused with
  "Devices are Device nodes in the graph now: leave device out, add a Device node and
  connect its Entities pin to each entity's Device pin".
- Turned off, the definition's entities stay in Home Assistant as unavailable. A
  definition saved off (a copy, or a new one saved with `enabled: false` through
  MCP) creates its entities the first time it is turned on.
- **Delete** is in a card's menu and, as **Delete entity definition**, at the bottom
  of the definition's form. Deleting removes the entities and the devices, the run
  history and the entities' stored states, and cannot be undone.
- An entity removed from a saved definition, or given another Type, is removed from
  Home Assistant when the saved definition next starts. So is an entity a C# device's
  code no longer adds, or a C# entity whose class now derives from another entity
  class.
- Saving compiles the code of each C# node. A save is refused while that code has
  errors, or while the node's settings are out of date with its code: open **Edit
  code** and apply it. Replacing a running definition stops its classes and makes new
  ones from the saved code: what a class keeps in its fields starts again, and its
  entities take back their states.
- A node library save rebuilds every definition, as it rebuilds every automation,
  and is refused when it would break one that works.
- An Entity node's state updates only for entities chosen on its steps' pins. An
  entity that a library or Custom C# step reads in its code (`NodeContext.State`)
  does not make the state follow it.
- Changing an Entity node's **Type** removes the values and wires of the pins the new
  type does not have, and says what it removed.
- When the stored states cannot be read, the log says "… the entities start with no
  value until they are set again".

### Commands

- Home Assistant checks number, select and text values and cover positions itself,
  and clamps a brightness or drops it for a light that cannot be dimmed, before
  HassSharp sees the command.
- A value the command did not send but its Entity node gives, such as a brightness
  of 255 the first time a dimmable light is turned on, appears in the trace as if it
  was sent.
- A light written in C# that is turned on without a brightness gets null, every
  time: keep its `Brightness` yourself.
- A number or text written in C# whose `Min` is above its `Max` refuses every value.
- A command or a callback that stops its own definition (turning it off through a
  service, say) waits out 10 seconds before it is cut off.

### Run history and traces

- A Trigger in a definition is not recorded unless you turn on its run history. A
  poll that fails then shows only in the log, as one line per reason.
- A trace shows a secret as hidden, but a true or false worked out from one as it
  is, and a step that returns what it was given can show it.
- Compare refuses a Text against a Number, and Entity field gives Text unless you
  choose another type: convert the state when you compare it with a number.
- A frame flow's failures show only in the log unless its run history is on.

### Secrets

- A secret setting's value is never sent to the panel or to an assistant as the
  setting's value, and is never kept in a draft. Traces and logs hide it, except as
  [Run history and traces](#run-history-and-traces) says (a true or false worked
  out from it, or what a step it was given returns), and the next bullet says what
  of a secret address is shown. A trace is what an assistant reads with `trace`.
- The host and port of an address that comes from a secret setting are not secret:
  failures, traces and the download log line name them. Its path, query and user
  information, and the headers and body, are never shown. A file a Download file
  step fetches from a secret address, with no **File name**, is saved as `file`; a
  C# node's `Files.Download` names it from the address unless it is given a name.
- A text entity shown as a password is not a secret: Home Assistant keeps its state
  in clear. Messages still do not quote it.

### HTTP and files

- `Http` and HTTP request keep no cookies: a definition that needs a session sends
  the `Cookie` header itself.
- A checksum is of the file as the server sends it, not of what a browser saves
  after decompressing it.
- A file already in `.storage/hass_sharp/files/` is trusted without hashing it again,
  and files no definition uses any more are never cleaned up.
- A redirect to another host fails an HTTP request step that has headers.

### Cameras

- A definition cannot take a picture of its own camera: it is refused at once. Two
  definitions that photograph each other's cameras time out.
- Pictures from another camera must be JPEG.
- Each camera in a chain keeps its own last good frame, so a chain of several
  cameras can show a picture that many times as old.
- Take pictures in a frame flow or a Trigger, not in When started: a camera that is
  not up when Home Assistant starts would keep the definition from starting.
- After a restart, a camera that is given its frames by a Trigger has no picture
  until that Trigger fires.
- A `SharpImage` is for one flow at a time.

## Through MCP

An assistant connected through Home Assistant's MCP Server builds definitions with
the `entities_*` tools: it reads the rules and the example definitions with
`get_capabilities` and `document: "entity"`, then previews, saves, turns on and off,
runs and reads the runs of a definition as the panel does. It writes each entity as
an Entity node of the graph, as the panel stores it: the relay of *Plug over HTTP*
is this node, and its two commands start the steps on these execution edges, which
name the command pin under `port`:

<!-- example: Plug over HTTP -->
```json
{
  "nodes": [
    {"id": "relay", "kind": "entity", "config": {"key": "relay", "domain": "switch", "name": "Relay", "deviceClass": "outlet", "arguments": {"device": {"kind": "unset"}}}, "position": {"x": 60, "y": 640}}
  ],
  "edges": [
    {"id": "relay:command:turn_on:switch_on", "source": "relay", "target": "switch_on", "port": "command:turn_on"},
    {"id": "relay:command:turn_off:switch_off", "source": "relay", "target": "switch_off", "port": "command:turn_off"}
  ]
}
```

A command's values are the node's data outputs `command:<command>:<value>`, such as
`command:turn_on:brightness` for a light, and a camera's frame flow starts at its
`frame` output. Each device is a Device node, wired to its entities as in
[Devices](#devices). A graph definition it sends is `{name, settings, logic}`: it
carries no `entities` list (one that does, even an empty one, is refused with
"Entities are Entity nodes in the graph now: leave entities out and add one Entity
node for each"), nor a `device` (one that is not null is refused with "Devices are
Device nodes in the graph now: leave device out, add a Device node and connect its
Entities pin to each entity's Device pin"), and `devices` sent back with it are ignored. A definition it reads
lists them under `entities` and `devices`, for reading only. A secret typed into a
conversation reaches the assistant's provider: enter secrets in the panel instead.
See [MCP authoring](mcp-authoring.md).
