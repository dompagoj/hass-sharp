import { createEffect, createSignal, on, Show, untrack } from 'solid-js'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { acceptRun } from '../components/runDispatch'
import { useHass } from '../context'
import type { EditorContext } from '../visual/adapter'
import { manualTriggers, RunNowControl } from '../visual/RunNow'
import type { EntityEditorDocument } from './adapter'
import { definitionsQueryKey, entityApi } from './api'
import type { EntityDocument } from './model'

const saveFirst = 'Save the entity definition to run this step'
const starting = 'The entity definition is still starting'
const notRunning = 'The entity definition is not running'

/** How many times a definition is read while it starts, every 2 s: about 30 s, then what the last read said. */
export const runStateReads = 15

/**
 * Run now of an entity definition: its Trigger steps, in the top bar where an automation's are, shown while the graph
 * or the saved definition has one. Unlike an automation's, it runs only the saved definition while it is on and runs
 * its saved revision, and not while there are unsaved changes; what may run is the answer's `runnable`, never worked
 * out here. A definition switched on or off here, whose switch says otherwise than its document, or still starting, is
 * read again every 2 s, at most {@link runStateReads} times, to learn when it runs. A read that finds it saved again or
 * switched elsewhere has the editor read it again (`reload`), so the page and its switch say what the server says.
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
  // Switched here (how many times since the document): the saved document's run state is older than the switch. Each
  // switch is a new query key, so its reads start again; a new document (a read, a save) is current, and starts at 0.
  const [switched, setSwitched] = createSignal(0)
  createEffect(on(() => context.enabled, () => setSwitched(count => count + 1), { defer: true }))
  createEffect(on(() => context.document, () => setSwitched(0), { defer: true }))
  const reread = () => {
    const document = context.document
    return !!document && context.enabled !== false && (switched() > 0 || document.status === 'setup' || document.enabled !== context.enabled)
  }
  const queryKey = () => [...definitionsQueryKey, 'run-state', context.document?.id, context.document?.revision, context.enabled, switched()]
  // The reads answered (or failed) under each key: a new document or switch starts again.
  const [answered, setAnswered] = createSignal<ReadonlyMap<string, number>>(new Map())
  const count = (key: readonly unknown[]) => answered().get(JSON.stringify(key)) ?? 0
  const fresh = useQuery(() => ({
    queryKey: queryKey(),
    queryFn: async ({ queryKey: key }: { queryKey: readonly unknown[] }) => {
      try {
        return await api.read(context.document!.id)
      } finally {
        setAnswered(current => new Map(current).set(JSON.stringify(key), (current.get(JSON.stringify(key)) ?? 0) + 1))
      }
    },
    enabled: reread(),
    staleTime: 0,
    // Until it has started (a read sent before the switch was taken still says it is off), a failed read too.
    refetchInterval: query => {
      const read = query.state.data
      if (read && settled(read)) return false
      return untrack(() => count(query.queryKey)) < runStateReads ? 2000 : false
    },
  }))
  const settled = (read: EntityDocument) => read.enabled === context.enabled && read.status !== 'setup'
  /** No more reads: as many as are made were made, and the last did not settle. */
  const exhausted = () => count(queryKey()) >= runStateReads
  /** The last successful read of the saved definition, if it is of this one. */
  const read = () => { const found = fresh.data, document = context.document; return found && document && found.id === document.id ? found : undefined }
  // Saved again elsewhere (at once), or still switched otherwise than here once no more reads are made: read again.
  createEffect(() => {
    const found = read(), document = context.document
    if (!reread() || !found || !document) return
    if (found.revision !== document.revision || (exhausted() && found.enabled !== context.enabled)) untrack(() => context.reload())
  })
  /** The saved definition's run state: the document, or its read once it is read again; undefined while that read waits. */
  const state = (): EntityDocument | undefined => {
    const document = context.document
    if (!document || !reread()) return document
    const found = read()
    return found && found.revision === document.revision && found.enabled === context.enabled ? found : undefined
  }
  const runnable = () => new Set((state()?.triggers ?? []).filter(trigger => trigger.runnable).map(trigger => trigger.triggerId))
  const choices = () => manualTriggers(context.document, context.catalog, context.names).filter(choice => runnable().has(choice.nodeId))
  const reason = () => {
    if (!context.document) return saveFirst
    if (context.conflict) return 'The saved entity definition changed elsewhere; choose what to keep first'
    if (context.dirty) return saveFirst
    if (context.catalogError) return 'The node catalog could not be loaded'
    if (context.enabled === false) return 'Turn the entity definition on to run this step'
    if (choices().length) return
    const current = state()
    // Still waiting while reads are made; after the last, only what is known: it is not running.
    if (!current) return reread() && !exhausted() ? starting : notRunning
    if (current.status === 'setup') return exhausted() ? notRunning : starting
    if (current.status !== 'running' || !current.currentRevisionActive) return notRunning
    return 'No Trigger step can be started with Run now'
  }
  return (
    <RunNowControl automationId={context.document?.automationId} choices={choices()} unavailable={reason()} menuLabel="Run which Trigger step"
      tip={choices().length > 1 ? 'Choose a Trigger step to run' : 'Run the saved entity definition’s Trigger step now'} busy={context.busy}
      onNotice={context.notice}
      start={choice => { const document = context.document!; return acceptRun(document.automationId, () => api.run(document.id, choice.nodeId), queryClient) }} />
  )
}
