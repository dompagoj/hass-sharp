# Domain-typed entity handles for node code: design

## Context

A custom node (a node library method or a Custom C# node) can only take an entity as a `string`
today. It cannot say "binary_sensor only", so the editor lets any entity be wired or picked, and
inside the code the state is untyped text.

The editor already has the other half: the `entity_id:<domain>` type family, a trigger "Entity"
output that becomes `entity_id:binary_sensor` when its entity is one, domain-limited pickers, and
wire checks that refuse the wrong domain. Only Call service's target uses it; C# node code cannot
name those types.

Commit `16795a6` removed an earlier generation of typed entities: a per-entity tree
(`Entities.Light.Kitchen`) on about 900 hand-written lines. This design is per domain and
generated from Home Assistant's own lists, with nothing per domain written by hand.

Outcome: `BinarySensorEntity motionSensor` on a node method is a pin only binary_sensor entities
reach, with a typed `State` and attributes inside the code, and the same members readable in the
graph through Select property.

## Decisions (yours)

1. **Live handle.** The class wraps an entity id; it is wired from a trigger's "Entity" output or
   picked on the node, and `.State` is read when the node runs.
2. **Only what Home Assistant declares.** `State` and enum-like attributes are typed from Home
   Assistant's lists; every other declared attribute is a `JsonElement` the code inspects or casts.
3. **One type per domain, generated at startup, plus Select property.** `LightEntity` is the C#
   face of the editor's existing `entity_id:light`.
4. **Saved enum values are Home Assistant's text** (`"on"`), not numbers.
5. **Compare gets a dropdown** of the enum's values on its unwired side when the other side is
   wired to an enum. The Constant and Convert type lists do not grow.
6. **Start from main** (flow outputs and .NET 11 are merged), in a new worktree.

## Goals, non-goals, anti-goals

Goals
- An `XEntity` parameter is a pin only that domain reaches, by wire or by the on-node picker.
  `Entity` accepts any domain. A returned handle is an `entity_id:<domain>` output that fits Call
  service's target and other entity inputs.
- Each domain class has a typed `State` and one property per attribute Home Assistant declares.
- Select property on an entity wire lists the same members; Compare can test an enum member
  against a picked value.
- Classes follow the running Home Assistant; no per-domain hand-written code.

Non-goals
- Service methods on the classes; handles are passed to `context.Services.*` instead.
- Typed state on `EntityStateSnapshot` ("To state", "From state", Read entity).
- Attribute types inferred from this install's entities, or from a hand-kept table.
- Per-entity classes; lists of handles as pins; per-domain pin colours.
- Narrowing a plain `entity_id` wire to a domain in the editor (Convert's type list does not
  offer `entity_id:<domain>` today; the validator accepts it). Noted as a follow-up.

Anti-goals
- A graph valid today becomes invalid or needs migrating.
- Home Assistant text reaching generated code other than as identifiers, string literals or
  escaped doc text (the rule `ServiceGenerator` keeps).
- One domain whose generated code fails to compile stopping node code that does not use it.
- A service registration marking open node libraries as changed.

## Approaches considered

- **A. One type per domain, generated at startup (chosen, with Select property).**
- **B. Classes as editor types of their own.** Two types per domain, fit rules on both
  validators, built-in ports and saved graphs changed. Rejected: several times the work.
- **C. Like A, generated at build time and shipped.** Frozen to one Home Assistant version.
  Rejected: startup generation was asked for.

## Data source (verified on Home Assistant 2026.11.1)

The `entity_component` translation category: keys
`component.<domain>.entity_component.<device_class|_>.state.<state>` and
`...state_attributes.<attribute>.{name, state.<value>}`. 56 domains have it; it yields 31 state
enums and about 42 attribute enums. `async_get_translations(hass, language, "entity_component",
integrations)` loads domains whose integration is not set up, but logs a warning for an unknown
one, so the list is filtered through `loader.async_get_integrations` first. Python type hints on
Home Assistant's entity classes matched 32 of 204 attributes and are not used.

## Design

### Types

Hand-written, `HassSharp.Entities` (added to `DiagnosticsProvider.GlobalUsings`):

```csharp
public class Entity   // any domain; implicit conversion to string
{
    public string EntityId { get; }
    public string RawState { get; }
    public double? NumericState { get; }       // RawState as a number, else null
    public JsonElement Attributes { get; }
    public JsonElement? Attribute(string name);
    public EntityStateSnapshot Snapshot();
}
```

`ServiceTarget` gains implicit conversions from `Entity?` and `Entity[]?` (declared only there, to
avoid ambiguity), so `context.Services.Light.TurnOn(light)` compiles.

Generated, same namespace, in an assembly of their own:

```csharp
public sealed class LightEntity : Entity
{
    public LightState State { get; }
    public LightColorMode? ColorMode { get; }   // values listed by Home Assistant
    public JsonElement? Brightness { get; }     // no listed values
}
public enum LightState { Off, On, Unknown, Unavailable, Other }
```

Rules (calls of mine are marked):
- **State:** an enum of the listed states plus `Unknown`, `Unavailable`, `Other`; off-list text
  reads as `Other`. No listed states, and `sensor`, give `string State`. *(mine)*
- **Attributes:** listed values give a nullable enum that also has `Other`; exactly false/true
  gives `bool?`; otherwise `JsonElement?`, null when absent. *(nullable and `Other` are mine)*
- **Same value list on several attributes of a domain** (`color_mode`,
  `supported_color_modes`): the shortest name is the enum, the rest stay `JsonElement?`. *(mine)*
- **Reads** go through `VisualRuntime.ReadEntity`: the trigger's state for the entity that
  started the run, the current state otherwise; a missing entity throws. *(mine)*
- **Enum text:** members map to Home Assistant's text through a generated table; that text is
  the value in saved graphs and traces.
- **Domains:** `homeassistant.const.Platform`, plus domains of existing entities, plus loaded
  components with `entity_component` data (helpers such as `input_boolean`, `person`, `timer`
  are not in `Platform`). A domain without metadata gets `string State` only. *(mine)*
- **Nullable pins:** `LightEntity?` or `= null` is an optional pin. Nullable handle results and
  handles inside lists or generics are refused with a diagnostic. *(mine)*

### Pins

- `XEntity` maps to catalog type `entity_id:<domain>`, `Entity` to `entity_id`, for parameters,
  results, tuple elements and flow values. Wires and traces keep carrying entity id strings.
- The runtime makes the handle when node code reads the pin and turns a returned handle back
  into its id; the domain pattern check stays as it is.
- Trigger outputs, pickers and wire checks are untouched.

### Select property and Compare

- The catalog gives `entity_id` the members of `Entity` and each domain the members of its
  class; both validators read them when resolving `entity_id[:domain]`.
- Generated enums are catalog types with text `enumValues` and Home Assistant's translated
  labels, flagged so type lists (Constant, Convert, loops) leave them out.
- Compare: when one side is wired to an enum type, the unwired side is a dropdown of that
  enum's values, stored as a literal of that type.

### Generation and delivery

- Python pushes the translations as JSON beside the service descriptions; C# normalises them,
  keeps the last schema in `.storage/hass_sharp/entity_schema.json`, and unites live with saved
  until Home Assistant has started.
- `EntityGenerator` emits the classes under `ServiceGenerator`'s text-safety rules. They are
  built into `HassSharpEntities_<hash>`, held beside the service calls in the one generated-code
  object every compilation takes, so a service push does not change the entities assembly.
- A generated `EntityHandles.Create(string entityId)` gives the handle for an id; the generated
  automation passes it to the runtime, so no `Type` is held across load contexts.

## Risks

- **Type identity across generations:** handles are made only by the factory the automation
  references; nothing is cached by `Type`.
- **Records holding a handle:** covered by the converter and trace handling in step 1.
- **Bundled frontend catalog:** knows no members or entity enums until the server catalog
  arrives, as with library types today.
- **Preview cost:** measured in step 6.

## Verification

- All five suites in `AGENTS.md`, plus the new `--entity-handles` suite.
- `python tests/packaging/sqlite_smoke.py` is unaffected and not rerun unless publish changes.
- Live, in the local container after publish, frontend build and restart:
  - a Custom C# node with `BinarySensorEntity motionSensor` wired from a State trigger on a
    binary sensor, reading `State`;
  - a light entity refused on that pin, by wire and in the picker;
  - Select property on the trigger's Entity output listing `State`, compared to On through the
    dropdown;
  - `context.Services.Light.TurnOn(light)` with a `LightEntity`;
  - a restart with the saved schema, checking automations compile before the first push.
