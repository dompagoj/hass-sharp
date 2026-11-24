import { nodeCatalog, type Graph, type NodeSpec, type VisualCatalogSnapshot } from '../src/visual/contract'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'

// The relays library's two classes, as the compiler writes their entries: taken from the shared fixture, which the C#
// suite holds to the compiler's, so they cannot drift from them.
export const relaysCase = (fixtureCases as unknown as { name: string; graph: unknown; catalogPatch?: { nodes: Record<string, NodeSpec> } }[])
  .find(c => c.name === 'library class node: a hub, and a relay on a Device node, are valid')!
/** The relays library's device class, Relay hub (category Relays; settings Address, required; Token, secret; Poll every). */
export const hubKind = 'library:relays:global%3A%3ANodeLib.RelayHub'
/** The relays library's entity class, Relay (category Custom; a switch; one setting, Address). */
export const relayKind = 'library:relays:global%3A%3ANodeLib.Relay'
/** The relays library's entries, a copy. */
export const relaysEntries = (): Record<string, NodeSpec> => structuredClone(relaysCase.catalogPatch!.nodes)
/** `base` (the built-in catalog) with the relays library's classes, or with `entries` in their place. */
export const withRelays = (base: VisualCatalogSnapshot = nodeCatalog, entries: Record<string, NodeSpec> = relaysEntries()): VisualCatalogSnapshot =>
  ({ ...base, nodes: { ...base.nodes, ...structuredClone(entries) } })
/** The shared case's graph, a copy: a hub (key hub), a Strip Device node and a relay (key relay) on the strip. */
export const relaysGraph = (): Graph => structuredClone(relaysCase.graph) as Graph
