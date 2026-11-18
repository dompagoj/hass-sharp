import { createEffect, createMemo, createSignal, Match, on, onCleanup, Switch, untrack } from 'solid-js'
import { useNavigate, useParams, useSearchParams } from '@solidjs/router'
import { Button, Icon } from '../components/Controls'
import { useHass } from '../context'
import { entityAdapter, entityTexts } from '../entities/adapter'
import { EntityApiError, entityApi, entityError } from '../entities/api'
import { ClassDefinitionEditor } from '../entities/ClassDefinitionEditor'
import type { EntityDocument } from '../entities/model'
import { EditorView } from './VisualAutomationView'

/** What the page shows: a definition's editor (a class's page or the graph editor), or why it shows none yet. */
type Page =
  | { kind: 'graph' }
  | { kind: 'class'; document?: EntityDocument }
  | { kind: 'loading' }
  | { kind: 'missing' }
  | { kind: 'failed'; error: unknown }

/**
 * An entity definition's page: `/entities/new` (`?logic=graph`, the default, or `?logic=class`; `?name=` names it,
 * `?type=` chooses a class's) and `/entities/<id>`. A saved definition is read first, and then opens in the editor of
 * its kind: one built from steps in the visual editor with the definition form (handed that read, so it is read once),
 * one built from a class in {@link ClassDefinitionEditor}. Neither editor shows while it is read.
 */
export function EntityDefinitionView() {
  const hass = useHass()
  const api = entityApi(hass)
  const route = useParams()
  const [params] = useSearchParams()
  const navigate = useNavigate()
  // The graph editor's first read of the definition this page read is that read.
  let handed: EntityDocument | undefined
  const base = entityAdapter(hass, {
    handed: id => { const document = handed?.id === id ? handed : undefined; handed = undefined; return document },
  })
  // The address an editor's own save goes to (a first save, a copy) keeps that editor: nothing is read again.
  let kept: string | undefined
  const adapter: typeof base = {
    ...base,
    save: async (...args) => { const result = await base.save(...args); kept = result.document.id; return result },
  }
  const address = () => (route.id === 'new' ? `new:${params.logic === 'class' ? 'class' : 'graph'}` : route.id ?? '')
  const pageFor = (at: string): Page => (at === 'new:class' ? { kind: 'class' } : { kind: 'graph' })
  const [page, setPage] = createSignal<Page>(untrack(address).startsWith('new:') ? pageFor(untrack(address)) : { kind: 'loading' })

  let generation = 0
  let disposed = false
  onCleanup(() => { disposed = true; generation++ })
  const open = async (id: string) => {
    const current = ++generation
    setPage({ kind: 'loading' })
    try {
      const document = await api.read(id)
      if (current !== generation) return
      if (document.logic.kind === 'graph') { handed = document; setPage({ kind: 'graph' }) }
      else setPage({ kind: 'class', document })
    } catch (error) {
      if (current !== generation) return
      setPage(error instanceof EntityApiError && error.code === 'not_found' ? { kind: 'missing' } : { kind: 'failed', error })
    }
  }
  createEffect(on(address, at => {
    if (at === kept) { kept = undefined; return }
    kept = undefined
    if (at.startsWith('new:')) { generation++; setPage(pageFor(at)); return }
    void open(at)
  }))

  const text = (value: unknown) => (typeof value === 'string' && value.trim() ? value : undefined)
  // The class page starts again for another definition (another object), but not for its own first save's address.
  const classPage = createMemo(() => { const current = page(); return current.kind === 'class' ? current : undefined })
  const classSaved = (document: EntityDocument) => {
    kept = document.id
    if (disposed || route.id === document.id) return
    navigate(`/entities/${encodeURIComponent(document.id)}`, { replace: true })
  }
  return (
    <Switch>
      <Match when={page().kind === 'graph'}>
        <EditorView adapter={adapter} />
      </Match>
      <Match when={classPage()} keyed>
        {current => <ClassDefinitionEditor document={current.document} name={untrack(() => text(params.name))} type={untrack(() => text(params.type))} onSaved={classSaved} />}
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
