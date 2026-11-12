import { A, useNavigate, type RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { useContext, For, Show, createEffect, createSignal, createMemo } from 'solid-js'
import type { UserScriptDTO } from '../types'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { Dialog } from '../components/Dialog'
import { Button, Field, Icon, Status } from '../components/Controls'
import { Menu } from '../components/Menu'
import { errorToHassError } from '../utils'

interface AutomationNameResponse {
  fileName: string
}

export const AutomationsListView = (_props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [nameDialogOpen, setNameDialogOpen] = createSignal(false)
  const [renameFile, setRenameFile] = createSignal<string>()
  const [name, setName] = createSignal('')
  const [error, setError] = createSignal<string>()
  const [submitting, setSubmitting] = createSignal(false)
  const [deleteFile, setDeleteFile] = createSignal<string>()
  const [deleteError, setDeleteError] = createSignal<string>()
  const [deleting, setDeleting] = createSignal(false)

  createEffect(() => {
    if (!nameDialogOpen()) {
      setRenameFile(undefined)
      setName('')
      setError(undefined)
    }
  })

  const query = useQuery(() => ({
    queryKey: ['automations'],
    queryFn: () => hass.callApi<UserScriptDTO[]>('GET', 'hass-sharp/automations'),
  }))

  const openNameDialog = (fileName?: string) => {
    setRenameFile(fileName)
    setName(fileName ?? '')
    setError(undefined)
    setNameDialogOpen(true)
  }

  const closeNameDialog = () => {
    if (!submitting()) setNameDialogOpen(false)
  }

  const submitAutomationName = async () => {
    const automationName = name().trim()
    if (!automationName || submitting()) return
    const fileName = renameFile()

    setSubmitting(true)
    setError(undefined)

    try {
      const result = await hass.callApi<AutomationNameResponse>(
        fileName ? 'PUT' : 'POST',
        fileName ? `hass-sharp/automations/${encodeURIComponent(fileName)}` : 'hass-sharp/automations',
        { name: automationName },
      )

      if (fileName) {
        queryClient.removeQueries({ queryKey: ['automations', fileName], exact: true })
        queryClient.removeQueries({ queryKey: ['automations', result.fileName], exact: true })
      }
      await queryClient.invalidateQueries({ queryKey: ['automations'] })
      setNameDialogOpen(false)
      if (!fileName) navigate(`/automations/${encodeURIComponent(result.fileName)}`)
    } catch (err) {
      const hassError = errorToHassError(err as Error)
      setError(
        hassError.body?.error ??
          hassError.error ??
          (fileName ? 'Unable to rename the automation.' : 'Unable to create the automation.'),
      )
    } finally {
      setSubmitting(false)
    }
  }

  const closeDeleteDialog = () => {
    if (deleting()) return
    setDeleteFile(undefined)
    setDeleteError(undefined)
  }

  const deleteAutomation = async () => {
    const fileName = deleteFile()
    if (!fileName || deleting()) return

    setDeleting(true)
    setDeleteError(undefined)

    try {
      await hass.callApi('DELETE', `hass-sharp/automations/${encodeURIComponent(fileName)}`)
      queryClient.removeQueries({ queryKey: ['automations', fileName], exact: true })
      await queryClient.invalidateQueries({ queryKey: ['automations'] })
      setDeleteFile(undefined)
    } catch (err) {
      const hassError = errorToHassError(err as Error)
      setDeleteError(hassError.body?.error ?? hassError.error ?? 'Unable to delete the automation.')
    } finally {
      setDeleting(false)
    }
  }

  const [search, setSearch] = createSignal('')
  const files = createMemo(() => {
    const term = search().trim().toLocaleLowerCase()
    return (query.data ?? []).filter(file => [file.fileName, ...file.classes.map(klass => klass.name)].some(name => name.toLocaleLowerCase().includes(term)))
  })

  return (
    <section class="hs-page" aria-labelledby="automations-title">
      <header class="hs-page-heading">
        <div><p class="hs-eyebrow">YOUR WORKSPACE</p><h1 id="automations-title">Automations</h1>
          <p class="hs-muted">A little C#. A home that works your way.</p></div>
        <Button variant="primary" onClick={() => openNameDialog()}><Icon name="plus" />New automation</Button>
      </header>
      <div class="hs-list-toolbar">
        <Field type="search" label="Search automations" placeholder="Search by file or class…" value={search()} onInput={event => setSearch(event.currentTarget.value)} />
        <span class="hs-muted hs-count" aria-live="polite">{files().length} {files().length === 1 ? 'file' : 'files'}</span>
      </div>
      <Show when={query.isPending}><div class="hs-empty" role="status">Loading automations…</div></Show>
      <Show when={query.isError}><div class="hs-notice hs-notice--danger" role="alert">
        <h2>Automations couldn’t load</h2><p>{errorToHassError(query.error!).body?.error ?? 'Check your connection and try again.'}</p>
        <Button onClick={() => void query.refetch()}>Try again</Button>
      </div></Show>
      <Show when={query.isSuccess && !files().length}><div class="hs-empty" role="status">
        <span class="hs-empty-icon"><Icon name="code" /></span>
        <h2>No automations{search() ? ' found' : ' yet'}</h2>
        <p>{search() ? 'Try another file or class name.' : 'Create your first C# automation to get started.'}</p>
        <Show when={!search()}><Button variant="primary" onClick={() => openNameDialog()}>New automation</Button></Show>
      </div></Show>
      <ul class="hs-automation-list" aria-label="Automation files">
        <For each={files()}>{file => <li class="hs-automation-card">
          <div class="hs-file-row">
            <span class="hs-file-icon"><Icon name="code" /></span>
            <A class="hs-file-link" href={`/automations/${encodeURIComponent(file.fileName)}`}><h2>{file.fileName}</h2>
              <span class="hs-muted">{file.classes.length} {file.classes.length === 1 ? 'automation class' : 'automation classes'}</span>
            </A>
            <Status>C#</Status>
            <Menu label={`Actions for ${file.fileName}`} disabled={submitting() || deleting()} items={[
              { label: 'Rename', onSelect: () => openNameDialog(file.fileName) },
              { label: 'Delete', danger: true, onSelect: () => { setDeleteError(undefined); setDeleteFile(file.fileName) } },
            ]} />
          </div>
          <Show when={file.classes.length}><ul class="hs-class-list" aria-label={`Classes in ${file.fileName}`}>
            <For each={file.classes}>{klass => <li>
              <A href={`/automations/${encodeURIComponent(file.fileName)}`}><span class="hs-class-mark" aria-hidden="true">↳</span>
                <span class="hs-class-name">{klass.name.split('.').pop()}</span>
                <span class="hs-muted">{klass.methods.length} {klass.methods.length === 1 ? 'method' : 'methods'}</span><Icon name="chevron" />
              </A>
            </li>}</For>
          </ul></Show>
        </li>}</For>
      </ul>
      <Dialog
        open={nameDialogOpen()}
        title={renameFile() ? 'Rename Automation' : 'New Automation'}
        width="small"
        preventClose={submitting()}
        onClose={closeNameDialog}
        footer={
          <>
            <Button variant="ghost" disabled={submitting()} onClick={closeNameDialog}>
              Cancel
            </Button>
            <Button
              variant="primary"
              disabled={!name().trim() || submitting()}
              busy={submitting()}
              onClick={() => void submitAutomationName()}
            >
              {renameFile() ? 'Rename' : 'Create'}
            </Button>
          </>
        }
      >
        <form
          class="hs-stack"
          onSubmit={event => {
            event.preventDefault()
            void submitAutomationName()
          }}
        >
          <Field label="Automation name" type="text" name="name" autofocus required value={name()} disabled={submitting()}
            hint="Choose a descriptive name. The .cs extension is added automatically."
            onInput={event => setName(event.currentTarget.value)} />
          <Show when={error()}>
            {message => (
              <div class="hs-error" role="alert">
                {message()}
              </div>
            )}
          </Show>
        </form>
      </Dialog>
      <Dialog
        open={deleteFile() !== undefined}
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
              disabled={!deleteFile() || deleting()}
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
            Delete <strong>{deleteFile()}</strong>? This will remove the script and all its automations. This cannot be
            undone.
          </p>
          <Show when={deleteError()}>
            {message => (
              <div class="hs-error" role="alert">
                {message()}
              </div>
            )}
          </Show>
        </div>
      </Dialog>
    </section>
  )
}
