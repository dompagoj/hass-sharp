import { createEffect, createSignal, on, Show } from 'solid-js'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { acceptRun } from '../components/runDispatch'
import { useHass } from '../context'
import type { EditorContext } from '../visual/adapter'
import { manualTriggers, RunNowControl } from '../visual/RunNow'
import type { EntityEditorDocument } from './adapter'
import { definitionsQueryKey, entityApi } from './api'
import type { EntityDocument } from './model'

const saveFirst = 'Save the entity definition to run this step'

/**
 * Run now of an entity definition: its Trigger steps, in the top bar where an automation's are, shown while the graph
 * or the saved definition has one. Unlike an automation's, it runs only the saved definition while it is on and runs
 * its saved revision, and not while there are unsaved changes; what may run is the answer's `runnable`, never worked
 * out here. A definition switched on or off here, or still starting, is read again to learn when it runs.
 */
export function DefinitionRunNow(props: { context: EditorContext<EntityEditorDocument> }) {
  const has = () => props.context.graph().nodes.some(node => node.kind === 'trigger') || !!props.context.document?.triggers?.length
  return <Show when={has()}><DefinitionRunControl context={props.context} /></Show>
}

function DefinitionRunControl(props: { context: EditorContext<EntityEditorDocument> }) {
  const hass = useHass()
  const queryClient = useQueryClient()
  const api = entityApi(hass)
  const context = props.context
  // Switched here: the saved document's run state is older than the switch. A new document (a read, a save) is current.
  const [switched, setSwitched] = createSignal(false)
  createEffect(on(() => context.enabled, () => setSwitched(true), { defer: true }))
  createEffect(on(() => context.document, () => setSwitched(false), { defer: true }))
  const reread = () => {
    const document = context.document
    return !!document && context.enabled !== false && (switched() || document.status === 'setup')
  }
  const fresh = useQuery(() => ({
    queryKey: [...definitionsQueryKey, 'run-state', context.document?.id, context.document?.revision, context.enabled, switched()],
    queryFn: () => api.read(context.document!.id),
    enabled: reread(),
    staleTime: 0,
    // Until it has started (a read sent before the switch was taken still says it is off).
    refetchInterval: query => (query.state.data && !settled(query.state.data) ? 2000 : false),
  }))
  const settled = (read: EntityDocument) => read.enabled === context.enabled && read.status !== 'setup'
  /** The saved definition's run state: the document, or its read once it is read again; undefined while that read waits. */
  const state = (): EntityDocument | undefined => {
    const document = context.document
    if (!document || !reread()) return document
    const read = fresh.data
    return read && read.id === document.id && read.revision === document.revision && read.enabled === context.enabled ? read : undefined
  }
  const runnable = () => new Set((state()?.triggers ?? []).filter(trigger => trigger.runnable).map(trigger => trigger.triggerId))
  const choices = () => manualTriggers(context.document, context.catalog, context.names).filter(choice => runnable().has(choice.nodeId))
  const reason = () => {
    if (!context.document) return saveFirst
    if (context.conflict) return 'The saved entity definition changed elsewhere; choose what to keep first'
    if (context.dirty) return saveFirst
    if (context.catalogError) return 'The node catalog could not be loaded'
    if (context.enabled === false) return 'Turn the entity definition on to run this step'
    const current = state()
    if (choices().length) return
    if (!current || current.status === 'setup') return 'The entity definition is still starting'
    if (current.status !== 'running' || !current.currentRevisionActive) return 'The entity definition is not running'
    return 'No Trigger step can be started with Run now'
  }
  return (
    <RunNowControl automationId={context.document?.automationId} choices={choices()} unavailable={reason()} menuLabel="Run which Trigger step"
      tip={choices().length > 1 ? 'Choose a Trigger step to run' : 'Run the saved entity definition’s Trigger step now'} busy={context.busy}
      onNotice={context.notice}
      start={choice => { const document = context.document!; return acceptRun(document.automationId, () => api.run(document.id, choice.nodeId), queryClient) }} />
  )
}
