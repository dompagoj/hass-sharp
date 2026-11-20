# Typed settings and typed services for C# entity and device nodes

Approved by the owner on 2026-11-20 as the proposal below. A short design: the change is an extension of two
things that already exist.

## Why

Writing a real sensor as a C# entity node showed three gaps between a C# entity or device class and the code of a
Custom C# step:

- `[Setting]` accepts only a string, bool, int, long, double or enum. A class cannot declare "this sensor reads that
  light": there is no way to give it an entity, so a generic C# sensor that takes another sensor as its input cannot
  be written. A Custom C# step can: a `LightEntity light` parameter is a pin with a picker that lists lights only.
- A setting cannot be required. An empty one is a default value, silently.
- Service calls from a class are `CallService("light.turn_on", …)` with a text id. Step code has typed calls,
  `context.Services.Light.TurnOn(light, brightnessPct: 30)`.

## Goals

1. A `[Setting]` property can be an entity handle: `Entity`, or any domain's generated class (`LightEntity`,
   `BinarySensorEntity`, …), shown as a pin of type `entity_id` or `entity_id:<domain>` with the picker limited to
   that domain. Its pin type comes from the code that gives a Custom C# step's parameter its pin type. (Narrowed
   while planning: the other types a step parameter accepts, such as a list or a class of the author's, get their
   value only from a wire out of a flow, and a setting has none; see Not goals.)
2. A setting is required when it would otherwise be null: its property is a string or an entity handle, is not
   nullable, and has no initializer. An empty pin is then a problem on the node, and the definition is not saved. A
   nullable property (`LightEntity?`) or one with an initializer (`= ""`) is optional. A number, a bool or an enum
   always has a value of its own and is never required.
3. The value is fixed when the definition starts, as settings are today: typed in the pin, or wired from a Setting
   node where the types allow it. An entity handle reads the entity live (`Light.State`, its attributes), and `Track`
   takes a handle as well as an entity id, so a class can follow the entity it was given.
4. A C# entity or device class calls services with typed methods: `await Services.Light.TurnOn(Light,
   brightnessPct: 30)`, the same generated classes step code uses. `CallService(text, …)` stays for an id known only
   at run time.

## Not goals

- A setting of a type that has no typed value in a pin (a list of entities, an object, a class of the author's).
- Wiring another node's live output (the result of an Or node, a value a flow computed) into a C# node so that the
  class is told when it changes. That is a different mechanism from settings (the Entity node's state inputs have
  it); it is not built here.
- A generated static tree of this Home Assistant's entities by name (`Entities.Light.Kitchen`). It was removed
  earlier and is not brought back here.
- Changing what a Custom C# step accepts, or what an automation generates: an automation's generated source stays
  byte-identical.

## Anti-goals

- A second type mapping. A setting's pin type comes from the same code that gives a step parameter its pin type; a
  type the step refuses, a setting refuses with the same sentence.
- A setting that is silently empty where the code needs it: a required setting never reaches `Setup()` unset.
- A secret entity. `Secret = true` on an entity setting is refused when the code is compiled: a secret takes its
  value from a Setting node only, and an entity pin takes none from there.

## Approaches

1. **Widen `[Setting]` with the Custom step's type mapping (chosen).** The compiler already turns a parameter's type
   into a pin type, knows the entity handles and their domains, and the editor already has the domain-limited
   picker for such a pin. A setting's signature entry carries that pin type; the validators check a setting pin as
   they check any pin of that type. Cost: every place that assumes a setting's type is one of five words has to
   learn the wider set.
2. **A new attribute for inputs (`[Input]`) beside `[Setting]`.** Keeps settings primitive and puts entities on a
   second kind of pin. Rejected: two attributes that both mean "a value fixed at start" with different type rules is
   the split this feature removes, and the name suggests live wires, which are not built.
3. **Constructor parameters as pins**, as a step's method parameters are. Rejected: the host constructs the class
   before it has the node's key or its settings, classes are created again on every save, and every existing class
   and document uses properties.

For services: **the generated service classes work from any caller that can make a service call**, a run's
`NodeContext` or a component, rather than a second generated set for components. One generated source, one set of
doc comments, and a service that goes away breaks both kinds of code at their next compile in the same way.

## Design

- **Signature.** A setting's entry in a node's `signature.settings[]` carries its pin type and whether it is
  required. The primitive types read as they do today, so a stored document of this version needs no change.
- **Values.** A typed value in the pin is a literal of the pin's type (an entity id for an entity pin). The host
  gives the property a handle of the declared class before `Setup()`.
- **Required.** Non-nullable and no initializer in the code means required. The rule is one sentence on the node,
  in both validators, at preview and at save.
- **Secrets.** `Secret = true` on an entity setting is refused when the code is compiled.
- **Definition settings and the Setting node.** Definition settings stay a string, a number or a boolean. An entity
  pin takes a typed value (the picker) and no wire: a Setting node's text is not an entity id to the type check.
- **Track.** `Track(Entity entity, …)` beside `Track(string entityId, …)`.
- **Services.** `protected … Services` on `SharpComponent`; a call is `CallService` with typed fields: it waits as
  `CallService` does today and works wherever `CallService` works, also from `Stop()`.
- **Editor.** The pin shows the entity picker for an entity type, limited to its domain; an empty required pin shows
  the problem; Apply after a code change keeps a value whose type still fits and says what it removed otherwise, as
  today. The code editor completes `LightEntity` and `Services` in a class's code.
- **MCP and documents.** The texts an assistant reads and the guide say what a setting can be, how a required one
  behaves, and show a sensor that follows another entity and calls a typed service.

## Left to the plan

Exact member names in the signature, the rule's code and sentence, how the generated service classes are made
caller-neutral without changing an automation's generated source, the order of tasks, and the tests.
