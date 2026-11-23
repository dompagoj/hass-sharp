import type { Accessor, Component, JSX } from 'solid-js'
import type { QueryClient } from '@tanstack/solid-query'
import type { Graph, GraphDocument, VisualCatalogSnapshot } from './contract'
import type { SourceRange, VisualDiagnostic, VisualPreview } from './api'
import type { VisualGraph } from './graph'
import type { SummaryNames } from './nodeSummary'
import type { RunNotice } from './RunNow'
import type { DrawerTab } from './BottomDrawer'

export { automationAdapter, automationTexts } from './automationAdapter'

/**
 * What the editor needs of a saved document, as an adapter's `read` and `save` give it. An adapter's own document
 * type may carry more members (an entity definition's live entities): the session keeps the object it was given and
 * hands it back as {@link EditorContext.document}.
 */
export interface EditorDocument {
  id: string
  revision: number
  /** The graph the editor edits (for an entity definition, its whole definition under `graph.definition`). */
  graph: VisualGraph
  source: string
  sourceMap: SourceRange[]
  /** The run history key (`run_history`, the Runs tab, the overview's last run). */
  automationId: string
  /** Off: nothing starts it by itself. Missing means on. */
  enabled?: boolean
  active?: boolean
  /** False when the saved revision is not the one running: Save then stores it again (to activate it). */
  currentRevisionActive?: boolean
  activationError?: string | null
  catalogGeneration?: string | null
  savedUtc?: string | null
}

/**
 * What Home Assistant has of a saved document's entities: the keys of the saved entities (each part of its entity's
 * identity there, so it no longer changes), the entity id it gave each one, by key, the keys of the saved devices
 * (each part of its device's identity, so it no longer changes either), and by the key of each device whose code adds its
 * entities (a C# device's), the entities Home Assistant has of it.
 */
export interface SavedEntities { keys: string[]; entityIds: Record<string, string>; deviceKeys: string[]; deviceEntities?: Record<string, DeviceEntity[]> }
/** An entity a device's code added, as its card lists it: its name and the entity id Home Assistant gave it (none yet). */
export interface DeviceEntity { name: string; entityId: string | null }
/** A badge of a node's card, by the node's key ({@link EditorAdapter.attention}): its count, and the drawer tab it opens. */
export interface NodeAttention { count(nodeKey: string): number; tab: string }

/** What a save answers. */
export interface EditorSaveResult<D extends EditorDocument = EditorDocument> {
  document: D
  active: boolean
  activationError: string | null
}

/**
 * Every document-kind-worded string of the editor shell, its session, dialogs and empty states. Strings about steps,
 * wires and nodes, true of any graph, stay in the editor.
 */
export interface EditorTexts {
  /** The editor page's accessible name ("Visual automation editor"). */
  editorLabel: string
  /** The canvas's accessible name ("Automation canvas"); read only, ", read only" follows it. */
  canvasLabel: string
  /** A new document's name when the address gives none ("Untitled visual automation"). */
  untitled: string
  /** The name field's accessible name and title ("Automation name"). */
  nameLabel: string
  /** The back button's name ("Back to automations"). */
  back: string
  /** The inspector's overview (nothing selected): its accessible name ("Automation overview"). */
  overviewLabel: string
  /** The Problems tab's heading of the problems about no step ("This automation"). */
  problemsHeading: string
  /** The Runs tab before the first save. */
  runsEmpty: string
  /** Export graph's file name (the graph is exported as `draft` gives it). */
  exportFileName: string
  /** The on/off switch's tooltip when on ("On: its triggers start it"). */
  enabledOn: string
  /** The on/off switch's tooltip when off ("Off: nothing starts it by itself; Run now still works"). */
  enabledOff: string
  /** The session's error when the saved revision changed elsewhere while there were unsaved changes here. */
  conflictMessage: string
  /** Save's title (and description) while that conflict is open. */
  conflictSaveTitle: string
  /** The conflict dialog's title. */
  conflictTitle: string
  /** The conflict dialog's explanation when it was met while editing. */
  conflictElsewhere: string
  /** The conflict dialog's explanation when it is a draft made on an older revision, given when the draft was made. */
  conflictDraft(time: string): string
  /** What "Save as a copy" makes, in "saves them as …, turned off" ("a new automation"). */
  copyNoun: string
  /**
   * The title of the bar that opens the overview where the inspector is a sheet and nothing is selected. Without it
   * (an automation) the overview is not offered there.
   */
  overviewPeek?: string
  /** The Runs tab's hint when a saved document has no runs yet (default: an automation's, "Run it now or wait for its next trigger."). */
  runsNone?: string
  /** A run's `revision` in the Runs tab (default: "Automation revision"). */
  runRevision?: string
  /** The run page's back button (default: "Back to the automation’s runs"). */
  traceBack?: string
  /** The run page's title when the run's graph has no name and the run no label (default: "Automation run"). */
  traceUntitled?: string
  /** The tooltip of "This run used an earlier version." (default: "The canvas shows the automation as it was for this run, not as it is now."). */
  traceEarlier?: string
  /** A run whose graph is no longer stored (default: "The automation as it was for this run is no longer stored. Its summary is below."). */
  traceMissing?: string
  /** A run without a step-by-step record (default: the automation's, "…The canvas shows the automation as it was for this run."). */
  traceNoRecord?: string
  /** The run page of a run that is not one of this document's (default: "This run is not one of this automation’s runs."). */
  traceForeign?: string
  /** The run page of a document that no longer exists (default: "This automation no longer exists."). */
  traceDeleted?: string
}

/**
 * The editor as an adapter's `runGroup` and `overview` see it. Every member but the functions is a getter: read it
 * where it is used (in JSX or an effect) and it stays current.
 */
export interface EditorContext<D extends EditorDocument = EditorDocument> {
  /** The saved document as `read`/`save` gave it; undefined before the first save (and while one loads). */
  readonly document: D | undefined
  /** The graph being edited. */
  graph: Accessor<VisualGraph>
  readonly catalog: VisualCatalogSnapshot
  readonly diagnostics: VisualDiagnostic[]
  /** Whether the document has ever been saved (it has an id and a revision). */
  readonly saved: boolean
  /** Changes the graph as one undo step (or joins the previous one with the same `coalesce` key). */
  commit(graph: VisualGraph, options?: { coalesce?: string }): void
  /** Registry names, so summaries name entities as the cards do. */
  readonly names: SummaryNames | undefined
  /** The on/off state shown (the switch's own answer first); undefined before the first save. */
  readonly enabled: boolean | undefined
  /** Unsaved changes to the graph. */
  readonly dirty: boolean
  /** Loading or saving: controls that act on the document wait. */
  readonly busy: boolean
  /** The saved revision changed elsewhere and the person has not chosen what to keep. */
  readonly conflict: boolean
  /** Why the node catalog could not be loaded, if so. */
  readonly catalogError: string | undefined
  /** Shows a notice under the top bar (an error stays until the next one). */
  notice(notice: RunNotice): void
  /**
   * Reads the saved document again, as a save that met a revision changed elsewhere does when nothing is unsaved: the
   * page, and the on/off switch, then show it as it is. Nothing happens while there are unsaved changes or a request.
   */
  reload(): void
  /** Selects a step (the overview's: and moves focus to it in the panel), or nothing. */
  select(id?: string): void
  /**
   * The overview's row a problem chosen in the Problems tab is about (`overviewProblem`'s answer), a new object each
   * time one is chosen and undefined once a step is selected: the overview opens that row and moves focus into it.
   */
  readonly located?: { target: string }
  /** The bottom drawer's tab shown, or undefined while the drawer is closed. */
  readonly drawerTab?: string
}

/**
 * One kind of document the visual editor edits: how it is read, checked, saved and switched, its ids, starter graph,
 * routes and words, and the parts of the editor only it has. The editor and its session ask the backend for the
 * document only through it; the catalog, the entity registry, node libraries, run history and traces are shared.
 */
export interface EditorAdapter<D extends EditorDocument = EditorDocument> {
  /** The kind of graph it edits; agrees with `graphDocument(graph)` of every graph it reads, starts or saves. */
  document: GraphDocument
  read(id: string): Promise<D>
  /** Checks a graph and generates its source (never stored). */
  preview(graph: VisualGraph): Promise<VisualPreview>
  /** `expectedRevision` 0 is a new document; `enabled` is given only then (a copy is saved turned off). */
  save(id: string, graph: VisualGraph, expectedRevision: number, options?: { enabled?: boolean }): Promise<EditorSaveResult<D>>
  /** Turns the saved document on or off and answers its state now; it keeps any cached lists in step itself. */
  setEnabled(id: string, enabled: boolean, options?: { queryClient?: QueryClient }): Promise<boolean>
  /** The backend's refusal in an error this adapter's calls threw (`conflict`, `validation` with a preview as detail). */
  apiError(error: unknown): { code: string; detail: unknown } | undefined
  /** The id of a new document (its first save, or a copy), from its graph. */
  newId(graph: VisualGraph): string | Promise<string>
  /** A new document's graph. */
  starter(name: string): VisualGraph
  /** The step selected when the editor opens, if any. */
  initialSelection?: string
  /** The graph as a draft keeps it in this browser, and as Export graph downloads it (an entity definition drops its secret values). */
  draft?(graph: VisualGraph): VisualGraph
  /**
   * The graph the editor goes on with once `sent` has been saved as `document`, in its place and without an undo step
   * (an entity definition's typed secrets become saved ones). Without it, `sent`.
   */
  savedGraph?(sent: VisualGraph, document: D): VisualGraph
  /** The graph renamed to `name` from the name field (an entity definition's first entity follows its name). Without it, only `name` changes. */
  rename?(graph: VisualGraph, name: string, state: { saved: boolean }): VisualGraph
  listHref: string
  editorHref(id: string): string
  runHref(id: string, runId: string): string
  /** The query keys whose queries a save (or a copy) makes stale: the lists that show the document. */
  queryKeys: readonly (readonly unknown[])[]
  texts: EditorTexts
  /** The top bar's run controls beside the on/off switch (admins only). */
  runGroup?(context: EditorContext<D>): JSX.Element
  /** The top of the inspector's overview, when nothing is selected (the problem count and last run follow it). */
  overview?(context: EditorContext<D>): JSX.Element
  /**
   * The saved document's entities as Home Assistant has them, for the inspector of a step that declares one (an
   * entity definition's Entity node): undefined before the first save; empty lists for a saved document with no
   * entities. An adapter of a document that has none (automations) leaves it out.
   */
  savedEntities?(context: EditorContext<D>): SavedEntities | undefined
  /** The icon of the bar that opens the overview where the inspector is a sheet (with `texts.overviewPeek`). */
  overviewIcon?: readonly string[]
  /**
   * The overview's row that shows a problem about no step (an entity definition's entity or setting), if any. Chosen
   * in the Problems tab, such a problem then selects nothing, shows the overview, and hands it the row as
   * `context.located` (a problem the overview lists at its top, its list). Without it (an automation) such a problem
   * selects nothing, as before.
   */
  overviewProblem?(diagnostic: VisualDiagnostic): string | undefined
  /**
   * Problems the editor finds in the graph itself besides the validator's, each about no step (an entity definition's
   * option left empty that its type needs): listed with the validator's, and an error blocks Save as one of its does.
   */
  check?(graph: VisualGraph, catalog: VisualCatalogSnapshot): VisualDiagnostic[]
  /**
   * What to tell the person once Save as a copy has saved `copied` (the graph as it was before the copy): what the
   * copy lacks (an entity definition's secrets that were not typed here). Without it, or an answer of undefined, nothing.
   */
  copyNotice?(copied: VisualGraph): string | undefined
  /**
   * What deleting the steps `ids` asks first, as a confirmation's title and text (an entity definition's Entity node
   * that steps left behind still use); undefined when it asks nothing. Without it, or with that answer, steps are
   * deleted at once.
   */
  removal?(graph: Graph, ids: string[], catalog: VisualCatalogSnapshot): { title: string; body: string } | undefined
  /**
   * Tabs of the bottom drawer only this kind of document has (an entity definition's Device tab), shown after Runs.
   * Asked once, when the editor opens; `?drawer=<id>` opens the drawer on one of them as on a tab of the editor's own.
   */
  drawerTabs?(context: EditorContext<D>): DrawerTab[]
  /**
   * What a C# device's or a C# entity's card shows as a badge beside its problems: `count` of its node's key (an entity
   * definition's: the cards its running revision's page waits on the person with), shown when above 0, and the drawer
   * tab (one of `drawerTabs`) that the badge opens. Asked once, when the editor opens; `count` is read where it is used,
   * so it stays current. Without it (an automation), no card shows one.
   */
  attention?(context: EditorContext<D>): NodeAttention
  /** The help dialog, opened from the "⋯" menu (no Help item without it). */
  help?: Component<{ open: boolean; onClose(): void }>
}
