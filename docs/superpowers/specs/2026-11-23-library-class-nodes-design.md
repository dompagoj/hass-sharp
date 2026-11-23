# Library classes as nodes: design

Status: design for the owner's approval. Nothing is built before approval. No push, tag or release without the
owner's go for that item.

## Context

A node library can hold a device or entity class, such as hass-sharp-vision's `PeopleDetector`. Today the only way to
use one is a **C# device** (or **C# entity**) node whose code derives from it:

```csharp
public class FrontDoorPeople : PeopleDetector { }
```

The owner's words: "i thought the sharp vision device once imported would become a node the user can just plug into
their graph, having to use a custom c# node that just inherits PeopleDetector is kind of silly".

A library's node *methods* already appear in the step list (`[Node]` on a static method, kind
`library:<library id>:<type>.<method>`). Its *classes* do not: `[Node]` on a class is only a title
(`HassSharp/Visual/NodeAttribute.cs`), and the test `ClassTitles` asserts that it makes no step.

This is the first of two projects agreed in this order; the second is face recognition in hass-sharp-vision, which
ships the People detector as such a node.

## Intent

A device or entity class in a node library can be offered as a node of its own. The user drops it on an entity
definition's canvas, fills in its settings, and writes no code.

## Goals

- A library class marked `[Node("People detector")]` that derives from `SharpDevice` or from an entity class
  (`SharpSwitch`, `SharpSensor`, `SharpCamera`, …) appears in the step list of entity definitions, under its
  `Category`, with its documentation summary as the description.
- A node of it has what a C# device or C# entity node has, except code: a key, for a device a name and "Part of", for
  an entity the Device pin, and the class's `[Setting]` properties as inputs, inherited ones first.
- It behaves as a C# node of that class does at run time: lifecycle, entities, device page, device data, settings,
  secrets.
- Deriving from a library class in a C# device or C# entity node keeps working unchanged.

## Non-goals

- Library classes as nodes in automations. They are for entity definitions only, as C# nodes are.
- Converting existing C# nodes automatically. A stored `csharp_device` node whose code only derives from a library
  class stays as it is and keeps working.
- Running a class's `[NodeSetup]` for a class node. A device has its own `Setup()`; `[NodeSetup]` stays what it is
  for node methods.
- Editing or overriding the class from the node. A user who wants to add to it derives from it in a C# node.
- A new look for the card. It is drawn as a C# device or C# entity card is, without the code button.

## Anti-goals

- A helper class of a library showing up as a step because it happens to derive from an entity class (the People
  detector's own `PeopleDetectorCamera` is one).
- A value typed into a secret setting's pin of such a node staying in a draft or an export. Today's stripping reads
  the built-in catalog only (`withoutTypedSecrets` in `frontend/src/entities/model.ts`), which does not know library
  kinds.
- A library save that removes or renames such a class leaving a definition that fails only when it starts. It must be
  refused or reported at the save, as a removed node method is.
- The generated source of any existing graph changing (`GeneratedSourcePinTests` pins it byte for byte).
- The two validators (C# and `contract.ts`) disagreeing about the new kind.

## Decisions (owner's)

- A library's device shows up as a node the user plugs into the graph, with no code to write.
- Build this before face recognition.

## Approaches considered

- **A node kind for library classes (chosen).** The catalog gets one entry per marked class; a node of it has no
  code, and HassSharp creates the library class itself. It removes the one-line class completely. It costs the most:
  everything that today reads "a node with settings has C# code" has to tell the two apart.
- **A step-list entry that drops a pre-filled C# device node.** Almost all frontend, and nothing else changes. But
  the user still gets a "C# device" node with a code button and a copy of the one-line class in every definition.
  Rejected: it hides the problem instead of removing it.
- **Every library class that derives from a device or entity class is a step, unmarked.** No attribute to learn, but
  helper classes show up too. Rejected for that.

The owner left the choice of approach to me.

## Design

Everything below is my call. Names are proposals; the builder may adjust them where the code suggests better ones,
keeping the behaviour.

### 1. Which classes become nodes

A class in a node library is a node when all of these hold:

- it has `[Node(title)]`;
- it derives from `SharpDevice`, or from an entity class that has a domain (the rule C# entity nodes use today);
- a definition can create it: public, top level, not abstract, not generic, with a public constructor without
  parameters (`EntityClasses.Creatable`).

`[Node]` on a library class that derives from a device or entity class but cannot be created (it is abstract, say) is
a library **warning** that says why it is not offered as a step. It is not an error: such a class may still be meant
only as a base. `[Node]` on a class in a C# node's own code stays a title only. `Category` is used as it is for a
method; `Pure` means nothing on a class.

The class's settings are checked once, as they are today for a library base class (`CheckLibrarySettings`).

### 2. The kind and its catalog entry

- Kind: `library:<library id>:<type identity>`, the form node methods use, without a method name. It must start with
  `library:` so that the existing "this step is no longer in its node library" message and the external import's
  rewrite of library ids apply to it.
- The entry says it declares a device or an entity, is offered to entity definitions only (`documents: ["entity"]`),
  keeps its values under `arguments`, and has the config of the matching C# node without `source` and `signature`:
  `key`, and for a device `name` and `via`. An entity's entry has the Device input.
- The class's settings signature (the shape `DescribeClassNode` builds today) is **part of the catalog entry**, not
  of the node's stored config. A node's inputs are read from the entry. So there is nothing to go stale in a stored
  graph, and no `deriveSignatures` step for these nodes.
- The catalog keeps, beside its node methods, what the generator needs to create the class (its type expression).

### 3. "Has settings" and "has code" become two things

Today `dynamicInputs: "settings"` means both "its inputs are a class's settings" and "it holds C# code in
`config.source`". The new kind has the first without the second. Every reader of that flag is sorted into one of the
two meanings, in C# (`VisualNodeCatalog.IsClassNode` / `HasCode`) and in `contract.ts` (`isClassNode` / `hasCode`):

- settings as inputs, required and secret settings, value checks, the component list of a definition, layout: both
  kinds;
- the code editor, the code button, `custom_preview`, `deriveSignatures`, the per-node code section of the generated
  source, the signature freshness check: C# nodes only.

Places that name `csharp_entity` and `csharp_device` directly are moved to the catalog entry's facts (declares a
device, declares an entity, has code), so that a library kind is treated as its C# counterpart wherever code is not
involved. Known ones: `DeclaredEntities`, `DeclaredDevices`, `CSharpDeviceMembers`, `CSharpEntities` in
`VisualNodeCatalog.cs` and their mirrors in `contract.ts`; `storingKinds`, `ownEntities`, `pageKinds`, `stepPickers`,
`CSharpNodeSection`, `nodeSummary`, `TraceInspector`, `cardEntities`, `renameEntityKey` in the frontend;
`_C_SHARP_NODES` in `external.py`.

### 4. Generated code and run time

- The generated factory creates the library class directly: `"<node id>" => new global::NodeLib.PeopleDetector()`.
  No class and no namespace is generated for the node.
- Settings are applied, and the node is set up, started and stopped, exactly as a C# node is. Its key identifies its
  device, its entities, its page and its data as a C# node's key does.
- Replacing a C# device node with the library node of its base class **under the same key** keeps the device, its
  entities' ids, its page and its stored data, because all of them are keyed by definition and node key. A test
  shows this; the user does it by hand (delete the old node, drop the new one, give it the old key).

### 5. When the library changes

A library save already previews every entity definition against the new libraries and refuses a save that would break
one, naming it.

- A class that is gone, or no longer a node, makes its nodes' kind unknown: the save is refused with the existing
  missing-library-node message.
- A setting that is gone, or whose type no longer takes the value a node has for it, is handled as a removed or
  retyped parameter of a node method is handled today. The builder reads what that is and keeps the two alike.
- A new **required** setting without a default makes every definition that uses the class invalid ("X is required"
  is a validation error today), so the library save is refused, naming them, as any breaking library change is. No
  carve-out is made. The guide tells library authors to give a new setting a default, or to ship the library together
  with a definition that sets it; hass-sharp-vision's new settings all get defaults.

### 6. The editor

- The step list of an entity definition shows the class under its category, by its title, with its description.
- Dropping it gives a node whose key is made from the class's title (`people_detector`, then `people_detector_2`
  when taken), and for a device a name that is the title. This is new for these nodes: a C# device's key is
  `device`, `device_2`, … today and a C# entity's comes from its domain, which stay as they are.
- The card and the right panel are a C# device's or C# entity's: settings as inputs, the entities area of a device,
  the waiting badge, the Device tab, the question before deleting a node with stored data. There is no code button
  and no code editor. The right panel says which library the node comes from.
- The question before deleting a node with stored data says what is true: the data is deleted when the definition is
  saved without a node of that key. Today it says "without this node", which is wrong for the swap of section 4,
  where a node with the same key is added before the save and the data is kept.
- Drafts and exports strip values typed into secret settings using the live catalog, for both kinds.

### 7. Import and the assistant

- **Add external:** an imported definition may hold such nodes. Their kinds are rewritten to the id the library is
  stored under, as method kinds are.
- **Values kept on overwrite.** When an import overwrites a definition, a value the user typed into a setting is
  kept if the stored node and the file's node have the same id, both are class nodes (C# or library, in either
  direction), and the file's node has a setting of that key that takes the value (its type, and for an entity its
  domain). Otherwise the file's value is used. The new node's settings are read from the catalog as it is after the
  import's libraries are saved; no stored signature of the old node is needed. Today `keep_device_inputs` skips a
  node whose kind changed, which would reset the Camera of every user's Front door people when hass-sharp-vision
  moves it to the new node.
- **MCP tools:** the catalog tools list the new kinds by themselves. The texts that describe C# nodes also say that
  a library may offer its classes as nodes.

### 8. Documentation

`docs/node-libraries.md` ("Entity classes") and `docs/custom-entities.md` describe `[Node]` on a class, the rules of
section 1, and that deriving still works. `NodeAttribute`'s summary is corrected.

### 9. What hass-sharp-vision does with it (in the recognition project's release, not here)

`PeopleDetector` gets `[Node("People detector", Category = "Vision")]`, loses `abstract`, and its constructor becomes
public (it is `protected` today, which alone would keep it from being offered); the bundled Front door people
definition uses the new node with the node id and key it has today, so an import that overwrites it keeps the user's
values; the README says which HassSharp it needs.

## Risks

- **The flag split touches many readers.** One sorted into the wrong group shows up as a code button on a node
  without code, or as a node whose settings are ignored. The shared validation fixture and the frontend tests are the
  guard.
- **The catalog now carries facts a stored node used to carry.** Everything that read `config.signature` for a class
  node must read the entry for a library kind; one that still reads the config sees no settings.
- **A kind with no method.** Code that takes every `library:` kind for a node method (`MethodOf`) must cope.

## Left to the builder

- The exact shape of the catalog entry and of the class descriptor.
- Whether the split is a second flag, a derived fact (`config.source` exists) or something else.
- The wording of the new warning and of the line that names the library on the right panel.
- The order of work.

## Verification

Tests, beside the suites in `AGENTS.md`:

- **Runtime:** a library with a marked device class and a marked entity class. Cover: both appear in the catalog for
  entity definitions and not for automations; an unmarked class and a marked abstract class do not, the second with
  its warning; a definition with such a node compiles, runs, creates its entities, applies its settings (a secret one
  from a Setting node), and shows a page and keeps data under its key; the generated source of existing graphs is
  unchanged; a library save that removes the class or a setting in use is refused or reported as section 5 says; a
  C# node deriving from the same class still works; swapping the C# node for the library node under the same key
  keeps entities and data.
- **Shared validation fixture** (`tests/fixtures/visual-graph-validation.json`): cases for the new kind, so both
  validators agree.
- **Python:** import rewrites the kind; an overwrite keeps typed values for library to library, C# to library and
  library to C# nodes of the same id, and uses the file's value where the kept one no longer fits; the catalog tools
  list the kind for entity definitions only.
- **Frontend:** the step list offers it in an entity definition only; the card has no code button, has the entities
  area and the waiting badge; the right panel edits key, name and settings; a typed secret is stripped from drafts
  and exports; deleting a node with stored data asks.

Live check in a throwaway `hass_wt` container on port 5002 (never the `hass` container, never the `mcp__hass__*`
tools): save a library with a marked device class that draws a page; drop it on a new definition, set its settings,
save; its entities appear and its page works; restart and re-save keep its data; replace a C# device node deriving
from the same class with the library node under the same key and confirm the entities and data are kept.
