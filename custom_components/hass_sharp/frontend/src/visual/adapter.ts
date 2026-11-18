import type { Accessor, Component, JSX } from 'solid-js'
import type { QueryClient } from '@tanstack/solid-query'
import type { GraphDocument, VisualCatalogSnapshot } from './contract'
import type { SourceRange, VisualDiagnostic, VisualPreview } from './api'
import type { VisualGraph } from './graph'
import type { SummaryNames } from './nodeSummary'
import type { RunNotice } from './RunNow'

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
  /** Selects a step (the overview's: and moves focus to it in the panel), or nothing. */
  select(id?: string): void
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
  /** The help dialog, opened from the "⋯" menu (no Help item without it). */
  help?: Component<{ open: boolean; onClose(): void }>
}
