import { createEffect, createMemo, createSignal, For, Index, onCleanup, Show, useContext } from 'solid-js'
import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { HassCtx } from '../context'
import { Button, Field, Icon } from '../components/Controls'
import { Dialog } from '../components/Dialog'
import { startOfToday } from '../automations/model'
import { focusSoon } from '../visual/soon'
import { createNotice } from '../visual/TopBar'
import { definitionsQueryKey, EntityApiError, entityApi, entityError, entityTypesQueryKey, type UnreadableDefinition } from '../entities/api'
import { DefinitionCard, definitionPollInterval, definitionsSummary, noteStarting, watchedEntityIds } from '../entities/DefinitionCard'
import { DeleteDefinitionDialog } from '../entities/DeleteDefinition'
import { liveState } from '../entities/liveState'
import type { HomeDefinition } from '../entities/model'

/** The two ways to build an entity definition, as "New entity definition" offers them. */
const starts = [
  { logic: 'graph', title: 'Build with nodes', description: 'Define entities and their behaviour with steps.' },
  { logic: 'class', title: 'Use a class from the node library', description: 'Create entities from a C# class in your node library, with its settings.' },
] as const
type Logic = (typeof starts)[number]['logic']

const byName = (a: HomeDefinition, b: HomeDefinition) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id.localeCompare(b.id)

/**
 * The Entities section: a card for each entity definition, with its status, its entities and their live states, its
 * switch and its menu; "New entity definition" names a draft and opens it in the editor, built with nodes or from a
 * class. The list is read as the automations page reads its own: again while a run is going or a definition starts,
 * and after a switch or a delete. A definition file the backend cannot read is listed apart, by its id and why, with
 * Delete (at revision 0) its only action.
 */
export const EntitiesListView = (_props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const api = entityApi(hass)
  const admin = () => !!hass.user?.is_admin
  let page!: HTMLElement
  const [notice, showNotice] = createNotice()

  // ---- The list: one request for every card. Read again (see definitionPollInterval) only while the page is visible.
  const startingSince = new Map<string, number>()
  // The files the list's last answer could not read, kept beside the cards (the query keeps the cards alone), and in
  // the cache under a key of their own, so that a page opened again over the cached cards has them too.
  const unreadableKey = [...definitionsQueryKey, 'unreadable']
  const [unreadable, setUnreadable] = createSignal(queryClient.getQueryData<UnreadableDefinition[]>(unreadableKey) ?? [])
  const home = useQuery(() => ({
    queryKey: [...definitionsQueryKey],
    queryFn: async () => {
      const answer = await api.homePage(startOfToday())
      queryClient.setQueryData(unreadableKey, answer.unreadable)
      setUnreadable(answer.unreadable)
      return answer.definitions
    },
    staleTime: 0,
    refetchInterval: query => {
      const items = query.state.data ?? [], now = Date.now()
      noteStarting(startingSince, items, now)
      return definitionPollInterval(items, now, startingSince)
    },
    refetchIntervalInBackground: false,
  }))
  const items = createMemo(() => [...(home.data ?? [])].sort(byName))
  const byId = createMemo(() => new Map(items().map(item => [item.id, item])))
  const ids = createMemo(() => items().map(item => item.id), undefined, { equals: (a, b) => a.length === b.length && a.every((id, i) => id === b[i]) })
  // One request for the whole page, only when a definition is built from a class.
  const types = useQuery(() => ({
    queryKey: [...entityTypesQueryKey],
    queryFn: () => api.types(),
    staleTime: 0,
    enabled: items().some(item => item.logic.kind === 'class'),
  }))
  const classTitle = (item: HomeDefinition) => {
    if (item.logic.kind !== 'class' || !types.isSuccess) return undefined
    const type = item.logic.type
    return types.data.types.find(found => found.id === type)?.title ?? null
  }
  // A node library save can change the classes offered and rebuild definitions (new revisions): both are read again.
  const librariesChanged = () => {
    void queryClient.invalidateQueries({ queryKey: [...entityTypesQueryKey] })
    void queryClient.invalidateQueries({ queryKey: [...definitionsQueryKey] })
  }
  window.addEventListener('hass-sharp-libraries-changed', librariesChanged)
  onCleanup(() => window.removeEventListener('hass-sharp-libraries-changed', librariesChanged))
  // One subscription for every card's entities.
  const states = liveState(hass, () => items().flatMap(watchedEntityIds))
  // The clock a button's last press is told from ("Pressed 5 minutes ago").
  const [now, setNow] = createSignal(Date.now())
  const clock = setInterval(() => setNow(Date.now()), 30_000)
  onCleanup(() => clearInterval(clock))

  // ---- Focus: after a delete it goes to the next card, else the previous one, else "New entity definition".
  const focusAfter = (id: string) => {
    const list = ids(), at = list.indexOf(id)
    const next = at < 0 ? undefined : list[at + 1] ?? list[at - 1]
    const card = (cardId: string) => [...page.querySelectorAll<HTMLElement>('[data-definition]')].find(found => found.dataset.definition === cardId)
    return () => focusSoon(() => (next ? card(next)?.querySelector<HTMLElement>('.hs-home-card__link') : page.querySelector<HTMLElement>('.hs-home-new')), page)
  }
  const refresh = () => queryClient.invalidateQueries({ queryKey: [...definitionsQueryKey] })

  // ---- New entity definition: named first, then a draft in the editor, built with nodes or from a class.
  const [newOpen, setNewOpen] = createSignal(false)
  const [name, setName] = createSignal('')
  const [logic, setLogic] = createSignal<Logic>('graph')
  createEffect(() => { if (!newOpen()) setName('') })
  const openNew = (start: Logic = 'graph') => { setName(''); setLogic(start); setNewOpen(true) }
  const closeNew = () => setNewOpen(false)
  const submitNew = () => {
    const wanted = name().trim()
    if (!wanted) return
    setNewOpen(false)
    navigate(`/entities/new?logic=${logic()}&name=${encodeURIComponent(wanted)}`)
  }

  // ---- On/off: shown at once (on is "Starting" until the list is read again), put back with a message on failure.
  const [toggling, setToggling] = createSignal<ReadonlySet<string>>(new Set())
  const show = (id: string, change: (item: HomeDefinition) => HomeDefinition) =>
    queryClient.setQueryData<HomeDefinition[]>([...definitionsQueryKey], list => list?.map(item => (item.id === id ? change(item) : item)))
  const toggle = async (item: HomeDefinition, enabled: boolean) => {
    if (toggling().has(item.id)) return
    setToggling(current => new Set(current).add(item.id))
    const before = { enabled: item.enabled, status: item.status }
    try {
      // A list request already on its way must not put the old state back.
      await queryClient.cancelQueries({ queryKey: [...definitionsQueryKey] })
      show(item.id, current => ({ ...current, enabled, status: enabled ? 'setup' : 'off' }))
      const answer = await api.setEnabled(item.id, enabled)
      show(item.id, current => ({ ...current, enabled: answer.enabled }))
      await refresh()
    } catch (error) {
      show(item.id, current => ({ ...current, ...before }))
      showNotice({ text: `Could not turn ${item.name} ${enabled ? 'on' : 'off'}: ${entityError(error)}`, error: true })
    } finally {
      setToggling(current => { const next = new Set(current); next.delete(item.id); return next })
    }
  }

  // ---- Delete, with the editor's confirmation. A refusal stays in it and the list is read again, so a retry deletes
  // the revision the list has then; a conflict (changed since the list was read) says so and offers Reload the list.
  // One already deleted elsewhere is gone, as asked: the dialog closes once the list is read again.
  const [deleteItem, setDeleteItem] = createSignal<{ id: string; name: string; revision: number }>()
  const [deleteError, setDeleteError] = createSignal<{ text: string; conflict: boolean }>()
  const [deleting, setDeleting] = createSignal(false)
  const askDelete = (item: { id: string; name: string; revision: number }) => { setDeleteError(undefined); setDeleteItem(item) }
  const closeDelete = () => { if (!deleting()) { setDeleteItem(undefined); setDeleteError(undefined) } }
  const confirmDelete = async () => {
    const item = deleteItem()
    if (!item || deleting()) return
    setDeleting(true)
    setDeleteError(undefined)
    const focusNext = focusAfter(item.id)
    const done = async () => {
      await refresh()
      setDeleting(false)
      setDeleteItem(undefined)
      focusNext()
    }
    try {
      await api.remove(item.id, byId().get(item.id)?.revision ?? item.revision)
      await done()
    } catch (error) {
      if (error instanceof EntityApiError && error.code === 'not_found') return await done().catch(() => setDeleteItem(undefined))
      const conflict = error instanceof EntityApiError && error.code === 'conflict'
      setDeleteError(conflict
        ? { text: 'This entity definition changed since the list was loaded. Reload the list, then delete it.', conflict }
        : { text: `Could not delete it: ${entityError(error)}`, conflict })
      // Delete waits for the list, so a retry takes its revision.
      await refresh().catch(() => {})
    } finally {
      setDeleting(false)
    }
  }

  const StartChoices = () => (
    <div class="hs-ent-starts">
      <For each={starts}>
        {start => (
          <button type="button" class="hs-ent-start" onClick={() => openNew(start.logic)}>
            <strong>{start.title}</strong>
            <small>{start.description}</small>
          </button>
        )}
      </For>
    </div>
  )

  return (
    <section class="hs-page hs-home" ref={page} aria-labelledby="entities-title">
      <header class="hs-home-heading">
        <div class="hs-home-heading__text">
          <h1 id="entities-title">Entities</h1>
          <Show when={home.isSuccess && definitionsSummary(items())}><p class="hs-muted hs-home-summary">{definitionsSummary(items())}</p></Show>
        </div>
        <div class="hs-home-heading__actions">
          <Button variant="primary" class="hs-home-new" onClick={() => openNew()}><Icon name="plus" />New entity definition</Button>
        </div>
      </header>
      <Show when={notice()}>{current => <p role={current().error ? 'alert' : 'status'} class="hs-notice hs-home-notice" classList={{ 'hs-notice--danger': !!current().error }}>{current().text}</p>}</Show>

      <Show when={home.isPending}>
        <ul class="hs-home-grid" aria-busy="true" aria-label="Loading entity definitions">
          <Index each={[0, 1, 2]}>{() => <li class="hs-home-card hs-home-card--skeleton hs-ent-card--skeleton" aria-hidden="true"><span /><span /><span /></li>}</Index>
        </ul>
        <p class="hs-visually-hidden" role="status">Loading entity definitions…</p>
      </Show>
      <Show when={home.isError}>
        <div class="hs-notice hs-notice--danger" role="alert">
          <h2>Entities couldn’t load</h2><p>{entityError(home.error)}</p>
          <Button onClick={() => void home.refetch()}>Try again</Button>
        </div>
      </Show>
      <Show when={home.isSuccess && !items().length && !unreadable().length}>
        <div class="hs-home-empty" role="status">
          <h2>No entity definitions yet</h2>
          <p>An entity definition creates new entities and devices in Home Assistant. Build what they do with nodes, or use a C# class from your node library.</p>
          <StartChoices />
        </div>
      </Show>
      <Show when={home.isSuccess && items().length}>
        <ul class="hs-home-grid" aria-label="Entity definitions">
          <For each={ids()}>
            {id => (
              <Show when={byId().get(id)}>
                {item => (
                  <DefinitionCard
                    item={item()}
                    classTitle={classTitle(item())}
                    classFailed={types.isError}
                    states={states()}
                    now={now()}
                    admin={admin()}
                    busy={deleting()}
                    switching={toggling().has(id)}
                    onToggle={enabled => void toggle(item(), enabled)}
                    onOpen={() => navigate(`/entities/${encodeURIComponent(id)}`)}
                    onDelete={() => askDelete(item())}
                  />
                )}
              </Show>
            )}
          </For>
        </ul>
      </Show>

      <Show when={home.isSuccess && unreadable().length}>
        <section class="hs-ent-unreadable" aria-labelledby="entities-unreadable-title">
          <h2 id="entities-unreadable-title">Cannot be read</h2>
          <ul class="hs-ent-unreadable__list" aria-label="Entity definitions that cannot be read">
            <For each={unreadable()}>
              {item => (
                <li class="hs-ent-unreadable__row" data-unreadable={item.id}>
                  <span class="hs-ent-unreadable__text">
                    <code class="hs-ent-unreadable__id">{item.id}</code>
                    <span class="hs-ent-unreadable__reason">{item.reason}</span>
                  </span>
                  <Show when={admin()}>
                    <Button variant="danger" disabled={deleting()} aria-label={`Delete ${item.id}`} onClick={() => askDelete({ id: item.id, name: item.id, revision: 0 })}>Delete</Button>
                  </Show>
                </li>
              )}
            </For>
          </ul>
        </section>
      </Show>

      <Dialog open={newOpen()} title="New entity definition" width="small" onClose={closeNew} footer={<>
        <Button variant="ghost" onClick={closeNew}>Cancel</Button>
        <Button variant="primary" disabled={!name().trim()} onClick={submitNew}>Continue</Button>
      </>}>
        <form class="hs-stack" onSubmit={event => { event.preventDefault(); submitNew() }}>
          <p class="hs-muted">Set it up in the editor, then save it: its entities appear in Home Assistant.</p>
          <Field label="Entity definition name" type="text" name="name" autofocus required value={name()}
            hint="You can change it in the editor." onInput={event => setName(event.currentTarget.value)} />
          <fieldset class="hs-ent-choices">
            <legend>How it works</legend>
            <For each={starts}>
              {start => (
                <label class="hs-ent-choice">
                  <input type="radio" name="logic" value={start.logic} checked={logic() === start.logic} onChange={() => setLogic(start.logic)} />
                  <span class="hs-ent-choice__text"><strong>{start.title}</strong><small>{start.description}</small></span>
                </label>
              )}
            </For>
          </fieldset>
        </form>
      </Dialog>
      <DeleteDefinitionDialog open={!!deleteItem()} name={deleteItem()?.name ?? ''} deleting={deleting()}
        error={deleteError() && <>
          {deleteError()!.text}
          <Show when={deleteError()!.conflict}><Button disabled={deleting()} onClick={() => { closeDelete(); void home.refetch() }}>Reload the list</Button></Show>
        </>}
        onCancel={closeDelete} onConfirm={() => void confirmDelete()} />
    </section>
  )
}
