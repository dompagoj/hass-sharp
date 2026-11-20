import { createEffect, createSignal, Match, on, onCleanup, Switch, untrack } from 'solid-js'
import { useNavigate, useParams } from '@solidjs/router'
import { Button, Icon } from '../components/Controls'
import { useHass } from '../context'
import { entityAdapter, entityTexts } from '../entities/adapter'
import { EntityApiError, entityApi, entityError } from '../entities/api'
import type { EntityDocument } from '../entities/model'
import { EditorView } from './VisualAutomationView'

/** What the page shows: the graph editor, or why it shows none yet. */
type Page =
  | { kind: 'graph' }
  | { kind: 'loading' }
  | { kind: 'missing' }
  | { kind: 'failed'; error: unknown }

/**
 * An entity definition's page: `/entities/new` (`?name=` names it; any other parameter is ignored) and
 * `/entities/<id>`. A saved definition is read first, and then opens in the visual editor with the definition form,
 * handed that read, so it is read once. The editor does not show while it is read.
 */
export function EntityDefinitionView() {
  const hass = useHass()
  const api = entityApi(hass)
  const route = useParams()
  const navigate = useNavigate()
  // The graph editor's first read of the definition this page read is that read.
  let handed: EntityDocument | undefined
  const base = entityAdapter(hass, {
    handed: id => { const document = handed?.id === id ? handed : undefined; handed = undefined; return document },
  })
  // The address the editor's own save goes to (a first save, a copy) keeps the editor: nothing is read again. Only the
  // editor still shown says so: a save that answers after the page moved on changes nothing here.
  let kept: string | undefined
  const adapter: typeof base = {
    ...base,
    save: async (...args) => {
      const issuing = untrack(page)
      const result = await base.save(...args)
      if (!disposed && untrack(page) === issuing) kept = result.document.id
      return result
    },
  }
  const address = () => (route.id === 'new' ? 'new' : route.id ?? '')
  const [page, setPage] = createSignal<Page>(untrack(address) === 'new' ? { kind: 'graph' } : { kind: 'loading' })

  let generation = 0
  let disposed = false
  onCleanup(() => { disposed = true; generation++ })
  const open = async (id: string) => {
    const current = ++generation
    setPage({ kind: 'loading' })
    try {
      const document = await api.read(id)
      if (current !== generation) return
      handed = document
      setPage({ kind: 'graph' })
    } catch (error) {
      if (current !== generation) return
      setPage(error instanceof EntityApiError && error.code === 'not_found' ? { kind: 'missing' } : { kind: 'failed', error })
    }
  }
  createEffect(on(address, (at, previous) => {
    // A new definition's page is shown from the start.
    if (previous === undefined && at === 'new') return
    if (at === kept) { kept = undefined; return }
    kept = undefined
    if (at === 'new') { generation++; setPage({ kind: 'graph' }); return }
    void open(at)
  }))

  return (
    <Switch>
      <Match when={page().kind === 'graph'}>
        <EditorView adapter={adapter} />
      </Match>
      <Match when={true}>
        <section class="hs-cdef" aria-label="Opening entity definition">
          <header class="hs-topbar">
            <button type="button" class="hs-topbar__icon" aria-label={entityTexts.back} title={entityTexts.back} onClick={() => navigate('/entities')}>
              <Icon name="arrow" />
            </button>
            <h1 class="hs-topbar__heading">Entity definition</h1>
          </header>
          <Switch>
            <Match when={page().kind === 'loading'}>
              <p class="hs-visual-alert hs-visual-alert--quiet" role="status">Loading saved entity definition…</p>
            </Match>
            <Match when={page().kind === 'missing'}>
              <p class="hs-visual-alert hs-notice--danger" role="alert">This entity definition was not found. It may have been deleted.</p>
            </Match>
            <Match when={(() => { const current = page(); return current.kind === 'failed' ? current : undefined })()}>
              {failed => (
                <div class="hs-visual-alert hs-notice--danger" role="alert">
                  Could not open the entity definition: {entityError(failed().error)}
                  <p><Button onClick={() => { if (route.id) void open(route.id) }}>Try again</Button></p>
                </div>
              )}
            </Match>
          </Switch>
        </section>
      </Match>
    </Switch>
  )
}
