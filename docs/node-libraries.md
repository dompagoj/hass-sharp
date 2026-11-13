# Custom C# node libraries

Create a file in the **Node libraries** view, or use the administrator MCP
`library_preview` / `library_save` tools. The source is wrapped inside namespace
`NodeLib`; provide namespace contents, not an outer file-scoped namespace.
Libraries compile together with generated entity symbols and the framework
references used by automations. Their source is stored in Home Assistant's
`.storage/hass_sharp/libraries` directory through the revision-checked authoring
store. Editing files directly bypasses the authoring conflict checks.

This complete harmless library accepts an entity snapshot, returns a record and
then accepts its interface in another typed node:

```csharp
public interface IReading { string State { get; } }
public record Reading(string State) : IReading;
public static class Samples
{
    /// <summary>Describe a detached Home Assistant entity.</summary>
    /// <param name="entity">The current entity snapshot.</param>
    /// <returns>A typed reading.</returns>
    [Node("Describe", Category = "Examples")]
    [return: Port("Reading")]
    public static Reading Describe([Port("Entity state")] EntityStateSnapshot entity)
        => new(entity.State);

    /// <summary>Format a reading without changing devices.</summary>
    [Node("Format reading")]
    public static string Format(IReading reading) => $"State: {reading.State}";
}
```

Save it with ID `example` and name `Example.cs`. Add the following execution chain:
**Manual trigger → Read entity → Describe → Format reading → Log**. Choose a real
entity ID in Read entity. Wire its `value` output to Describe's `entity` input,
Describe's `result` to Format reading's `reading`, and Format reading's `result` to
Log's `value`. The catalog supplies those exact custom kinds and CLR types; the
interface assignment is checked by Roslyn. The complete graph JSON, complete
library source and the other authoring examples ship in
[`authoring_examples.json`](../custom_components/hass_sharp/authoring_examples.json)
and are returned by `get_capabilities`. Preview the graph, save, then manually run
its `start` trigger to write a log entry. Reading state does not control a device.

`[Node]` marks public static methods on public nongeneric containing types.
`[Port]` labels parameters and return ports; XML summary/parameter/return comments
become catalog documentation. Parameter names are port IDs. Optional parameters
retain their C# defaults. A `CancellationToken` parameter is supplied by the
runtime and is not a graph input. Synchronous void and nongeneric Task/ValueTask
produce execution only; generic Task/ValueTask results are awaited and become the
`result` port. Preview inspects symbols without invoking attributes, static
initializers or methods.

Ports support actual CLR classes, records, interfaces, enums, nullable types,
arrays and closed generics. Use the catalog's `assignableTo`, `nullable` and
`literalKind` metadata; arbitrary object types do not automatically gain a JSON
literal editor.

Ports carry the CLR type, never a refined catalog type. A `string` parameter
accepts an `entity_id` or `entity_id:light` value, because those refine
`string`; a `string` result does not fit an `entity_id` input such as Read
entity's, or a domain-filtered service target, because a string is not known to
be an entity ID. Pass such a result through a Convert node set to the input's type
(`entity_id`, or `entity_id:light` for a light target). An `EntityStateSnapshot`
parameter can be wired directly from Read entity or from a trigger's New state and
Previous state outputs. Open generic methods/containers, ref/in/out parameters, ref
returns, pointer/ref-like ports, static abstract interface methods and async void
are rejected. Custom kinds include source ID and the fully qualified method and
parameter signature; changing a type can change a kind. Renaming a file preserves
its stable ID and kinds.

The library editor supports full Roslyn completion, diagnostics, hover, signature
help with named parameters, formatting, and cross-file go-to-definition. Drafts
use immutable multi-file workspaces. The frontend discards obsolete responses
using model/workspace identities; saved catalog generation differs from the draft
source generation. A rejected save preserves the prior source and active graphs.
A successful save/rename/delete recompiles all graphs and advances their revisions.
Deleting a still-referenced type or method is rejected with dependency diagnostics.

## Scope snapshots

Branch, loop and structured error scopes isolate values through detached deep
snapshots. An entity snapshot is immutable. JSON nodes, arrays, lists,
dictionaries and ordinary user classes/records are copied; aliases within one
scope remain aliases within that scope. Nullable values retain their null state.
A value being compile-time assignable does not guarantee that it can be copied at
a scope boundary.

Live resources such as streams, tasks, delegates, safe handles and unsupported
framework object graphs fail with a precise snapshot error. For custom resource
containers implement `IVisualSnapshot.CreateVisualSnapshot()` to return a detached
instance of the same concrete type, deeply copying every reachable mutable
object. Returning the original object or a shallow copy violates the contract.
Keep IO/resources inside the method body; return data suitable for the next scope.
