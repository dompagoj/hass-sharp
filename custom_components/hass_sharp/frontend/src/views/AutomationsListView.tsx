import { librariesChanged } from '../visual/library-api'
import { A, useNavigate, type RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { useContext, For, Show, createEffect, createSignal, createMemo } from 'solid-js'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { Dialog } from '../components/Dialog'
import { Button, Field, Icon } from '../components/Controls'
import { Menu } from '../components/Menu'
import { visualApi, visualError, type VisualDocumentSummary } from '../visual/api'
import { entityQueryKey } from '../entities'

export const AutomationsListView = (_props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [refreshing, setRefreshing] = createSignal(false)
  const [refreshMessage, setRefreshMessage] = createSignal('')
  const [refreshFailed, setRefreshFailed] = createSignal(false)
  const refreshEntities = async () => {
    if (refreshing() || !hass.user?.is_admin) return
    setRefreshing(true); setRefreshFailed(false); setRefreshMessage('Refreshing entity IDs…')
    try {
      const result = await hass.callWS<{ success: boolean }>({ type: 'hass_sharp/reload_entities' })
      if (!result.success) throw new Error('Refresh failed')
      librariesChanged()
      await queryClient.invalidateQueries({ queryKey: entityQueryKey })
      setRefreshMessage('Entity IDs refreshed. New completions, diagnostics and compilations use the updated IDs. Running automations are unchanged.')
    } catch {
      setRefreshFailed(true); setRefreshMessage('Could not refresh entity IDs. Try again.')
    } finally { setRefreshing(false) }
  }
  const [nameDialogOpen, setNameDialogOpen] = createSignal(false)
  const [name, setName] = createSignal('')
  const visual = visualApi(hass)
  const [deleteVisual, setDeleteVisual] = createSignal<VisualDocumentSummary>()
  const [deleteError, setDeleteError] = createSignal<string>()
  const [deleting, setDeleting] = createSignal(false)

  createEffect(() => {
    if (!nameDialogOpen()) setName('')
  })

  const visualQuery = useQuery(() => ({ queryKey: ['visual-automations'], queryFn: visual.list, staleTime: 0 }))

  const openNameDialog = () => {
    setName('')
    setNameDialogOpen(true)
  }

  const closeNameDialog = () => setNameDialogOpen(false)

  // A new automation is a named draft in the editor; it is stored when it is first saved.
  const submitAutomationName = () => {
    const automationName = name().trim()
    if (!automationName) return
    setNameDialogOpen(false)
    navigate(`/visual/new?name=${encodeURIComponent(automationName)}`)
  }

  const closeDeleteDialog = () => {
    if (deleting()) return
    setDeleteVisual(undefined)
    setDeleteError(undefined)
  }

  const deleteAutomation = async () => {
    const document = deleteVisual()
    if (!document || deleting()) return

    setDeleting(true)
    setDeleteError(undefined)

    try {
      await visual.delete(document.id, document.revision)
      await queryClient.invalidateQueries({ queryKey: ['visual-automations'] })
      setDeleteVisual(undefined)
    } catch (err) {
      setDeleteError(`${visualError(err)} Reload the list before deleting a changed graph.`)
    } finally {
      setDeleting(false)
    }
  }

  const [search, setSearch] = createSignal('')
  const visualDocuments = createMemo(() => {
    const term = search().trim().toLocaleLowerCase()
    return (visualQuery.data ?? []).filter(document => document.graph.name.toLocaleLowerCase().includes(term))
  })
  const count = () => visualDocuments().length

  return (
    <section class="hs-page" aria-labelledby="automations-title">
      <header class="hs-page-heading">
        <div><p class="hs-eyebrow">YOUR WORKSPACE</p><h1 id="automations-title">Automations</h1>
          <p class="hs-muted">Build automations as graphs. A home that works your way.</p></div>
        <div class="hs-visual-actions"><Show when={hass.user?.is_admin}><Button busy={refreshing()} onClick={() => void refreshEntities()}>Refresh entity IDs</Button></Show><Button variant="primary" onClick={() => openNameDialog()}><Icon name="plus" />New automation</Button></div>
      </header>
      <Show when={refreshMessage()}><p role={refreshFailed() ? 'alert' : 'status'} class="hs-notice">{refreshMessage()}</p></Show>
      <div class="hs-list-toolbar">
        <Field type="search" label="Search automations" placeholder="Search by name…" value={search()} onInput={event => setSearch(event.currentTarget.value)} />
        <span class="hs-muted hs-count" aria-live="polite">{count()} {count() === 1 ? 'automation' : 'automations'}</span>
      </div>
      <Show when={visualQuery.isPending}><div class="hs-empty" role="status">Loading automations…</div></Show>
      <Show when={visualQuery.isError}><div class="hs-notice hs-notice--danger" role="alert">
        <h2>Automations couldn’t load</h2><p>{visualError(visualQuery.error)}</p>
        <Button onClick={() => void visualQuery.refetch()}>Try again</Button>
      </div></Show>
      <Show when={visualQuery.isSuccess && !count()}><div class="hs-empty" role="status">
        <span class="hs-empty-icon"><Icon name="code" /></span>
        <h2>No automations{search() ? ' found' : ' yet'}</h2>
        <p>{search() ? 'Try another name.' : 'Create your first automation to get started.'}</p>
        <Show when={!search()}><Button variant="primary" onClick={() => openNameDialog()}>New automation</Button></Show>
      </div></Show>
      <ul class="hs-automation-list" aria-label="Automations">
        <For each={visualDocuments()}>{document => <li class="hs-automation-card"><div class="hs-file-row">
          <span class="hs-file-icon"><Icon name="code" /></span>
          <A class="hs-file-link" href={`/visual/${encodeURIComponent(document.id)}`}><h2>{document.graph.name}</h2><span class="hs-muted">Saved graph · revision {document.revision}</span></A>
          <Menu label={`Actions for ${document.graph.name}`} disabled={deleting()} items={[
            { label: 'Edit / rename', onSelect: () => navigate(`/visual/${document.id}`) },
            { label: 'Delete', danger: true, onSelect: () => { setDeleteError(undefined); setDeleteVisual(document) } },
          ]} />
        </div></li>}</For>
      </ul>
      <Dialog
        open={nameDialogOpen()}
        title="New Automation"
        width="small"
        onClose={closeNameDialog}
        footer={
          <>
            <Button variant="ghost" onClick={closeNameDialog}>
              Cancel
            </Button>
            <Button variant="primary" disabled={!name().trim()} onClick={submitAutomationName}>
              Open draft
            </Button>
          </>
        }
      >
        <form
          class="hs-stack"
          onSubmit={event => {
            event.preventDefault()
            submitAutomationName()
          }}
        >
          <p class="hs-muted">Preview and save your graph in Home Assistant, then run it and inspect its history.</p>
          <Field label="Automation name" type="text" name="name" autofocus required value={name()}
            hint="Give your automation a name. You can change it in the editor."
            onInput={event => setName(event.currentTarget.value)} />
        </form>
      </Dialog>
      <Dialog
        open={!!deleteVisual()}
        title="Delete Automation"
        width="small"
        preventClose={deleting()}
        onClose={closeDeleteDialog}
        footer={
          <>
            <Button variant="ghost" disabled={deleting()} onClick={closeDeleteDialog}>
              Cancel
            </Button>
            <Button
              variant="danger"
              disabled={!deleteVisual() || deleting()}
              busy={deleting()}
              onClick={() => void deleteAutomation()}
            >
              Delete
            </Button>
          </>
        }
      >
        <div class="hs-stack">
          <p>
            Delete <strong>{deleteVisual()?.graph.name}</strong>? It stops running and its graph is removed. This cannot be
            undone.
          </p>
          <Show when={deleteError()}>
            {message => (
              <div class="hs-error" role="alert">
                {message()}
                <Button onClick={() => { closeDeleteDialog(); void visualQuery.refetch() }}>Reload the list</Button>
              </div>
            )}
          </Show>
        </div>
      </Dialog>
    </section>
  )
}
