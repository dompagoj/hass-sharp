import { For, Show } from 'solid-js'
import { HelpDialog } from '../components/HelpDialog'
import { setAutomationEnabled } from '../automations/enabled'
import type { HomeAssistant } from '../types'
import type { EditorAdapter, EditorContext, EditorTexts } from './adapter'
import { visualApi, VisualApiError, type VisualDocument } from './api'
import type { VisualCatalogSnapshot } from './contract'
import { Glyph } from './Glyph'
import { createGraph, type VisualGraph } from './graph'
import { icons } from './icons'
import { createUuid } from './id'
import { nodeSummary, type SummaryNames } from './nodeSummary'
import { RunNowButton } from './RunNow'

/** The automation editor's words. */
export const automationTexts: EditorTexts = {
  editorLabel: 'Visual automation editor',
  canvasLabel: 'Automation canvas',
  untitled: 'Untitled visual automation',
  nameLabel: 'Automation name',
  back: 'Back to automations',
  overviewLabel: 'Automation overview',
  problemsHeading: 'This automation',
  runsEmpty: 'Save this automation to see its runs here.',
  exportFileName: 'automation.graph.json',
  conflictMessage: 'This automation changed elsewhere while you had unsaved changes here.',
  conflictSaveTitle: 'The saved automation changed elsewhere: choose what to keep',
  conflictTitle: 'This automation changed elsewhere',
  conflictElsewhere: 'It was saved again somewhere else while you had unsaved changes here. Saving a node library does this to every automation that uses it.',
  conflictDraft: time => `Your unsaved changes from ${time} were made before this automation was saved again somewhere else.`,
  copyNoun: 'a new automation',
}

/** Why the saved automation cannot be run now, if so. */
const runUnavailable = (context: EditorContext<VisualDocument>) => {
  const doc = context.document
  if (!doc) return
  if (context.conflict) return 'The saved automation changed elsewhere; choose what to keep first'
  if (context.catalogError) return 'The node catalog could not be loaded'
  if (!doc.currentRevisionActive) return 'The saved version is not active'
}

/**
 * The visual automation editor: the `hass_sharp/visual` API, `visual-<uuid>` ids, the trigger → condition → service
 * starter, `/automations` and `/visual/<id>`, Run now in the top bar and the triggers in the overview.
 */
export function automationAdapter(hass: Pick<HomeAssistant, 'callWS'>): EditorAdapter<VisualDocument> {
  const api = visualApi(hass)
  return {
    document: 'automation',
    read: id => api.read(id),
    preview: graph => api.preview(graph),
    save: (id, graph, expectedRevision, options) => api.save(id, graph, expectedRevision, options),
    setEnabled: (id, enabled, options = {}) => setAutomationEnabled(hass, options.queryClient, id, enabled),
    apiError: error => (error instanceof VisualApiError ? error : undefined),
    newId: () => `visual-${createUuid()}`,
    starter: createGraph,
    initialSelection: 'trigger-1',
    listHref: '/automations',
    editorHref: id => `/visual/${id}`,
    runHref: (id, runId) => `/visual/${encodeURIComponent(id)}/runs/${encodeURIComponent(runId)}`,
    queryKeys: [['visual-automations']],
    texts: automationTexts,
    runGroup: context => (
      <RunNowButton document={context.document} catalog={context.catalog} names={context.names} unavailable={runUnavailable(context)}
        dirty={context.dirty} busy={context.busy} onNotice={context.notice} />
    ),
    overview: context => (
      <AutomationOverview graph={context.graph()} catalog={context.catalog} names={context.names} enabled={context.enabled} onSelect={context.select} />
    ),
    help: HelpDialog,
  }
}

/** The top of an automation's overview: whether it is off, and its triggers (each selects itself). */
export function AutomationOverview(props: {
  graph: VisualGraph
  catalog: VisualCatalogSnapshot
  names?: SummaryNames
  /** False when the saved automation is turned off; undefined for a draft. */
  enabled?: boolean
  onSelect(id: string): void
}) {
  const triggers = () => props.graph.nodes.filter(n => n.kind === 'trigger')
  return (
    <>
      <Show when={props.enabled === false}>
        <p class="hs-insp-off" role="note"><strong>Off:</strong> nothing starts this automation by itself. Run now still works.</p>
      </Show>
      <h2 class="hs-insp-heading">Starts when</h2>
      <Show when={triggers().length} fallback={<p class="hs-muted">This automation has no trigger yet.</p>}>
        <ul class="hs-insp-triggers">
          <For each={triggers()}>
            {node => (
              <li>
                <button type="button" class="hs-insp-trigger-line" onClick={() => props.onSelect(node.id)}>
                  <span class="hs-insp-trigger-line__icon" aria-hidden="true"><Glyph paths={icons.trigger} /></span>
                  <span>{nodeSummary(node, props.catalog, props.names, props.graph)}</span>
                </button>
              </li>
            )}
          </For>
        </ul>
      </Show>
    </>
  )
}
