import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { useContext, For, Show, createEffect, createSignal, createMemo, onCleanup, Index } from 'solid-js'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { Dialog } from '../components/Dialog'
import { Button, Field, Icon } from '../components/Controls'
import { visualApi, visualError, type HomeAutomation } from '../visual/api'
import { useRegistry } from '../registry'
import { createUuid } from '../visual/id'
import { focusSoon } from '../visual/soon'
import { createNotice } from '../visual/TopBar'
import { AutomationCard } from '../automations/AutomationCard'
import { GraphThumbnail } from '../automations/GraphThumbnail'
import { homeQueryKey, setAutomationEnabled } from '../automations/enabled'
import {
  filters, homeSummary, loadPreferences, pollInterval, matchesSearch, outlineGraph, passes, savePreferences, sortAutomations, sorts, startOfToday,
  triggerSummaries, type Filter, type Sort,
} from '../automations/model'

/** The empty page's drawing: a trigger, a check and an action, wired in a row. */
const example = {
  name: 'Example',
  nodes: [{ kind: 'trigger', x: 0, y: 40 }, { kind: 'condition', x: 380, y: 40 }, { kind: 'service', x: 760, y: 0 }, { kind: 'delay', x: 760, y: 180 }],
  edges: [[0, 1], [1, 2], [1, 3]] as [number, number][],
}

export const AutomationsListView = (_props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const visual = visualApi(hass)
  const admin = () => !!hass.user?.is_admin
  let page!: HTMLElement
  const [notice, showNotice] = createNotice()

  // ---- The list: one request for every card (outlines, health and recent runs). While a run is going it is read
  // again (see pollInterval), only while the page is visible: the query pauses its interval in a background tab.
  const home = useQuery(() => ({
    queryKey: [...homeQueryKey],
    queryFn: () => visual.home(startOfToday()),
    staleTime: 0,
    refetchInterval: query => pollInterval(query.state.data ?? [], Date.now()),
    refetchIntervalInBackground: false,
  }))
  const registry = useRegistry()
  const names = () => (registry.isSuccess ? registry.data : undefined)
  const [now, setNow] = createSignal(Date.now())
  const clock = setInterval(() => setNow(Date.now()), 30_000)
  onCleanup(() => clearInterval(clock))
  const items = () => home.data ?? []
  const byId = createMemo(() => new Map(items().map(item => [item.id, item])))
  const summaries = createMemo(() => new Map(items().map(item => [item.id, triggerSummaries(outlineGraph(item.outline), names())])))

  // ---- Search, filter and sort; the filter and sort are remembered in this browser.
  const saved = loadPreferences()
  const [search, setSearch] = createSignal('')
  const [filter, setFilter] = createSignal<Filter>(saved.filter)
  const [sort, setSort] = createSignal<Sort>(saved.sort)
  createEffect(() => savePreferences({ filter: filter(), sort: sort() }))
  const filterCount = (id: Filter) => items().filter(item => passes(item, id)).length
  const shown = createMemo(() =>
    sortAutomations(items().filter(item => passes(item, filter()) && matchesSearch(search(), item.outline.name, summaries().get(item.id) ?? [])), sort()).map(item => item.id))
  const narrowed = () => !!search().trim() || filter() !== 'all'
  const clearTools = () => { setSearch(''); setFilter('all') }
  const noResults = () => {
    const term = search().trim(), chosen = filters.find(f => f.id === filter())!
    if (term && filter() !== 'all') return `No automations match “${term}” in ${chosen.label.toLowerCase()}`
    if (term) return `No automations match “${term}”`
    return { attention: 'No automation needs attention', today: 'No automation ran today', never: 'Every automation has run', off: 'No automation is off', all: '' }[filter()]
  }

  // ---- Focus: after a rename, duplicate or delete it goes to a card that is on the page.
  const cardElement = (id: string) => [...page.querySelectorAll<HTMLElement>('[data-automation]')].find(card => card.dataset.automation === id)
  const focusCard = (id: string, part: 'link' | 'menu' = 'link') =>
    focusSoon(() => cardElement(id)?.querySelector<HTMLElement>(part === 'menu' ? '.hs-menu > button' : '.hs-home-card__link'), page)
  /** Where focus goes once `id` is gone: the next card, else the previous one, else "New automation". */
  const focusAfter = (id: string) => {
    const list = shown(), at = list.indexOf(id)
    const next = list[at + 1] ?? list[at - 1]
    return () => (next ? focusCard(next) : focusSoon(() => page.querySelector<HTMLElement>('.hs-home-new'), page))
  }

  // ---- New automation: a named draft in the editor, stored when it is first saved.
  const [nameDialogOpen, setNameDialogOpen] = createSignal(false)
  const [name, setName] = createSignal('')
  createEffect(() => { if (!nameDialogOpen()) setName('') })
  const openNameDialog = () => { setName(''); setNameDialogOpen(true) }
  const closeNameDialog = () => setNameDialogOpen(false)
  const submitAutomationName = () => {
    const automationName = name().trim()
    if (!automationName) return
    setNameDialogOpen(false)
    navigate(`/visual/new?name=${encodeURIComponent(automationName)}`)
  }

  // ---- On/off: optimistic, rolled back with a message when it fails; never navigates.
  const [toggling, setToggling] = createSignal<ReadonlySet<string>>(new Set())
  const toggle = async (item: HomeAutomation, enabled: boolean) => {
    if (toggling().has(item.id)) return
    setToggling(current => new Set(current).add(item.id))
    try {
      await setAutomationEnabled(hass, queryClient, item.id, enabled)
    } catch (error) {
      showNotice({ text: `Could not turn ${item.outline.name} ${enabled ? 'on' : 'off'}: ${visualError(error)}`, error: true })
    } finally {
      setToggling(current => { const next = new Set(current); next.delete(item.id); return next })
    }
  }

  // ---- Rename and duplicate save the stored graph again (read first: the list has no graphs).
  const [renaming, setRenaming] = createSignal<HomeAutomation>()
  const [newName, setNewName] = createSignal('')
  const [renameError, setRenameError] = createSignal<string>()
  const [saving, setSaving] = createSignal(false)
  const openRename = (item: HomeAutomation) => { setRenameError(undefined); setNewName(item.outline.name); setRenaming(item) }
  const closeRename = () => { if (!saving()) setRenaming(undefined) }
  const refresh = () => queryClient.invalidateQueries({ queryKey: [...homeQueryKey] })
  const rename = async () => {
    const item = renaming(), wanted = newName().trim()
    if (!item || !wanted || saving()) return
    if (wanted === item.outline.name) return setRenaming(undefined)
    setSaving(true); setRenameError(undefined)
    try {
      const document = await visual.read(item.id)
      if (document.revision !== item.revision) throw new Error('It was changed elsewhere. Reload the list and try again.')
      await visual.save(item.id, { ...document.graph, name: wanted }, document.revision)
      await refresh()
      setRenaming(undefined)
      focusCard(item.id, 'menu')
    } catch (error) {
      setRenameError(visualError(error))
    } finally { setSaving(false) }
  }
  const [duplicating, setDuplicating] = createSignal(false)
  const duplicate = async (item: HomeAutomation) => {
    if (duplicating()) return
    setDuplicating(true)
    try {
      const document = await visual.read(item.id)
      const id = `visual-${createUuid()}`, copyName = `${document.graph.name} (copy)`
      // The copy starts off, so it does not run its startup trigger or answer the original's triggers a second time.
      await visual.save(id, { ...document.graph, name: copyName }, 0, { enabled: false })
      await refresh()
      showNotice({ text: `Copy of ${document.graph.name} was created turned off.` })
      focusCard(id)
    } catch (error) {
      showNotice({ text: `Could not duplicate ${item.outline.name}: ${visualError(error)}`, error: true })
      focusCard(item.id, 'menu')
    } finally { setDuplicating(false) }
  }

  // ---- Delete, with the confirmation.
  const [deleteVisual, setDeleteVisual] = createSignal<HomeAutomation>()
  const [deleteError, setDeleteError] = createSignal<string>()
  const [deleting, setDeleting] = createSignal(false)
  const closeDeleteDialog = () => {
    if (deleting()) return
    setDeleteVisual(undefined)
    setDeleteError(undefined)
  }
  const deleteAutomation = async () => {
    const item = deleteVisual()
    if (!item || deleting()) return
    setDeleting(true)
    setDeleteError(undefined)
    try {
      const focusNext = focusAfter(item.id)
      await visual.delete(item.id, item.revision)
      await refresh()
      setDeleting(false)
      setDeleteVisual(undefined)
      focusNext()
    } catch (err) {
      setDeleteError(`${visualError(err)} Reload the list before deleting a changed graph.`)
    } finally {
      setDeleting(false)
    }
  }

  const summary = () => homeSummary(items())
  return (
    <section class="hs-page hs-home" ref={page} aria-labelledby="automations-title">
      <header class="hs-home-heading">
        <div class="hs-home-heading__text">
          <h1 id="automations-title">Automations</h1>
          <Show when={home.isSuccess && summary()}><p class="hs-muted hs-home-summary">{summary()}</p></Show>
        </div>
        <div class="hs-home-heading__actions">
          <Button variant="primary" class="hs-home-new" onClick={() => openNameDialog()}><Icon name="plus" />New automation</Button>
        </div>
      </header>
      <Show when={notice()}>{current => <p role={current().error ? 'alert' : 'status'} class="hs-notice hs-home-notice" classList={{ 'hs-notice--danger': !!current().error }}>{current().text}</p>}</Show>

      <Show when={!home.isSuccess || items().length}>
        <div class="hs-home-tools" role="search" aria-label="Find automations">
          <Field class="hs-home-search" type="search" label="Search" placeholder="Name or what starts it…" value={search()} onInput={event => setSearch(event.currentTarget.value)} />
          <div class="hs-home-filters" role="group" aria-label="Show">
            <For each={filters}>
              {option => (
                <button type="button" class="hs-home-filter" aria-pressed={filter() === option.id} onClick={() => setFilter(option.id)}>
                  {option.label}
                  <Show when={home.isSuccess && option.id !== 'all'}><span class="hs-home-filter__count">{filterCount(option.id)}</span></Show>
                </button>
              )}
            </For>
          </div>
          <label class="hs-home-sort">
            <span>Sort</span>
            <select class="hs-input" value={sort()} onChange={event => setSort(event.currentTarget.value as Sort)}>
              <For each={sorts}>{option => <option value={option.id}>{option.label}</option>}</For>
            </select>
          </label>
        </div>
      </Show>

      <Show when={home.isPending}>
        <ul class="hs-home-grid" aria-busy="true" aria-label="Loading automations">
          <Index each={[0, 1, 2]}>{() => <li class="hs-home-card hs-home-card--skeleton" aria-hidden="true"><span /><span /><span /></li>}</Index>
        </ul>
        <p class="hs-visually-hidden" role="status">Loading automations…</p>
      </Show>
      <Show when={home.isError}>
        <div class="hs-notice hs-notice--danger" role="alert">
          <h2>Automations couldn’t load</h2><p>{visualError(home.error)}</p>
          <Button onClick={() => void home.refetch()}>Try again</Button>
        </div>
      </Show>
      <Show when={home.isSuccess && !items().length}>
        <div class="hs-home-empty" role="status">
          <GraphThumbnail outline={example} class="hs-thumb hs-home-empty__drawing" />
          <h2>No automations yet</h2>
          <p>An automation is a small graph: a trigger says when it starts, and the steps after it check things and act on your home.</p>
          <Button variant="primary" onClick={() => openNameDialog()}><Icon name="plus" />New automation</Button>
        </div>
      </Show>
      <Show when={home.isSuccess && items().length && !shown().length}>
        <div class="hs-empty hs-home-none" role="status">
          <h2>{noResults()}</h2>
          <Button onClick={clearTools}>Clear</Button>
        </div>
      </Show>
      <Show when={home.isSuccess && shown().length}>
        <p class="hs-visually-hidden" aria-live="polite">{narrowed() ? `${shown().length} of ${items().length} automations shown` : ''}</p>
        <ul class="hs-home-grid" aria-label="Automations">
          <For each={shown()}>
            {id => (
              <Show when={byId().get(id)}>
                {item => (
                  <AutomationCard
                    item={item()}
                    names={names()}
                    now={now()}
                    admin={admin()}
                    busy={deleting()}
                    switching={toggling().has(id)}
                    onToggle={enabled => void toggle(item(), enabled)}
                    onRename={() => openRename(item())}
                    onDuplicate={() => void duplicate(item())}
                    onViewRuns={() => navigate(`/visual/${encodeURIComponent(id)}?drawer=runs`)}
                    onDelete={() => { setDeleteError(undefined); setDeleteVisual(item()) }}
                    onRunNotice={run => {
                      showNotice(run.error ? { text: `${item().outline.name}: ${run.text}`, error: true } : { text: `Run of ${item().outline.name} started.` })
                      if (!run.error) void refresh()
                    }}
                  />
                )}
              </Show>
            )}
          </For>
        </ul>
      </Show>

      <Dialog open={nameDialogOpen()} title="New automation" width="small" onClose={closeNameDialog} footer={<>
        <Button variant="ghost" onClick={closeNameDialog}>Cancel</Button>
        <Button variant="primary" disabled={!name().trim()} onClick={submitAutomationName}>Open draft</Button>
      </>}>
        <form class="hs-stack" onSubmit={event => { event.preventDefault(); submitAutomationName() }}>
          <p class="hs-muted">Build it in the editor, then save it to Home Assistant, run it and look at its runs.</p>
          <Field label="Automation name" type="text" name="name" autofocus required value={name()}
            hint="You can change it in the editor." onInput={event => setName(event.currentTarget.value)} />
        </form>
      </Dialog>
      <Dialog open={!!renaming()} title="Rename automation" width="small" preventClose={saving()} onClose={closeRename} footer={<>
        <Button variant="ghost" disabled={saving()} onClick={closeRename}>Cancel</Button>
        <Button variant="primary" busy={saving()} disabled={!newName().trim()} onClick={() => void rename()}>Rename</Button>
      </>}>
        <form class="hs-stack" onSubmit={event => { event.preventDefault(); void rename() }}>
          <Field label="Name" type="text" name="rename" autofocus required value={newName()} onInput={event => setNewName(event.currentTarget.value)} />
          <Show when={renameError()}>{message => <div class="hs-error" role="alert">Could not rename: {message()}</div>}</Show>
        </form>
      </Dialog>
      <Dialog open={!!deleteVisual()} title="Delete automation" width="small" preventClose={deleting()} onClose={closeDeleteDialog} footer={<>
        <Button variant="ghost" disabled={deleting()} onClick={closeDeleteDialog}>Cancel</Button>
        <Button variant="danger" disabled={!deleteVisual() || deleting()} busy={deleting()} onClick={() => void deleteAutomation()}>Delete</Button>
      </>}>
        <div class="hs-stack">
          <p>Delete <strong>{deleteVisual()?.outline.name}</strong>? It stops running and its graph is removed. This cannot be undone.</p>
          <Show when={deleteError()}>
            {message => (
              <div class="hs-error" role="alert">
                {message()}
                <Button onClick={() => { closeDeleteDialog(); void home.refetch() }}>Reload the list</Button>
              </div>
            )}
          </Show>
        </div>
      </Dialog>
    </section>
  )
}
