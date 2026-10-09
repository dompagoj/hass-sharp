import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { useContext, For, Show, Suspense, createEffect, createSignal } from 'solid-js'
import type { UserScriptDTO } from '../types'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { Dialog } from '../components/Dialog'
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

  return (
    <div class="w-full overflow-y-auto p-4">
      <div class="flex justify-end mb-4">
        <ha-button onClick={() => openNameDialog()}>New Automation</ha-button>
      </div>
      <ha-md-list class="w-full p-0! rounded-md">
        <Suspense fallback={<span>Loading...</span>}>
          <For each={query.data}>
            {file => (
              <>
                <ha-md-list-item>
                  <div slot="headline" class="font-bold text-blue-400">
                    {file.fileName}
                  </div>
                  <ha-icon slot="start" icon="mdi:file-code-outline" class="text-blue-400"></ha-icon>
                  <div slot="end" class="flex items-center gap-[4px]">
                    <ha-icon-button
                      label={`Rename ${file.fileName}`}
                      prop:disabled={submitting() || deleting()}
                      onClick={(event: MouseEvent) => {
                        event.stopPropagation()
                        openNameDialog(file.fileName)
                      }}
                    >
                      <ha-icon icon="mdi:pencil-outline" />
                    </ha-icon-button>
                    <ha-icon-button
                      label={`Delete ${file.fileName}`}
                      class="text-[var(--error-color)]"
                      prop:disabled={submitting() || deleting()}
                      onClick={(event: MouseEvent) => {
                        event.stopPropagation()
                        setDeleteError(undefined)
                        setDeleteFile(file.fileName)
                      }}
                    >
                      <ha-icon icon="mdi:delete-outline" />
                    </ha-icon-button>
                  </div>
                </ha-md-list-item>
                <div class="flex flex-col">
                  <For each={file.classes}>
                    {(klass, idx) => (
                      <ha-md-list-item type="button" href={`/hass-sharp/automations/${file.fileName}`}>
                        <div slot="headline">{klass.name.split('.').pop()}</div>
                        <div slot="supporting-text" class="text-xs text-gray-400">
                          {klass.methods.length} methods
                        </div>
                        <ha-icon slot="start" icon="mdi:code-braces" class="ml-4 opacity-70"></ha-icon>
                        <ha-icon-button slot="end">
                          <ha-icon icon="mdi:chevron-right"></ha-icon>
                        </ha-icon-button>
                        {idx() < file.classes.length - 1 && (
                          <div slot="bottom" class="border-b border-gray-800 ml-16"></div>
                        )}
                      </ha-md-list-item>
                    )}
                  </For>
                </div>
              </>
            )}
          </For>
        </Suspense>
      </ha-md-list>
      <Dialog
        open={nameDialogOpen()}
        title={renameFile() ? 'Rename Automation' : 'New Automation'}
        width="small"
        preventClose={submitting()}
        onClose={closeNameDialog}
        footer={
          <>
            <ha-button
              slot="secondaryAction"
              appearance="plain"
              prop:disabled={submitting()}
              onClick={closeNameDialog}
            >
              Cancel
            </ha-button>
            <ha-button
              slot="primaryAction"
              prop:disabled={!name().trim() || submitting()}
              prop:loading={submitting()}
              onClick={() => void submitAutomationName()}
            >
              {renameFile() ? 'Rename' : 'Create'}
            </ha-button>
          </>
        }
      >
        <form
          class="flex flex-col gap-[12px]"
          onSubmit={event => {
            event.preventDefault()
            void submitAutomationName()
          }}
        >
          <label class="flex flex-col gap-[8px]">
            <span>Automation name</span>
            <input
              class="block box-border h-[44px] w-full rounded-[4px] border border-[var(--divider-color)] bg-[var(--card-background-color)] px-[12px] py-0 text-[16px] leading-[24px] text-[var(--primary-text-color)] focus:outline-[var(--primary-color)]"
              type="text"
              name="name"
              autofocus
              required
              value={name()}
              disabled={submitting()}
              onInput={event => setName(event.currentTarget.value)}
            />
          </label>
          <Show when={error()}>
            {message => (
              <div class="text-sm text-[var(--error-color)]" role="alert">
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
            <ha-button
              slot="secondaryAction"
              appearance="plain"
              prop:disabled={deleting()}
              onClick={closeDeleteDialog}
            >
              Cancel
            </ha-button>
            <ha-button
              slot="primaryAction"
              variant="danger"
              prop:disabled={!deleteFile() || deleting()}
              prop:loading={deleting()}
              onClick={() => void deleteAutomation()}
            >
              Delete
            </ha-button>
          </>
        }
      >
        <div class="flex flex-col gap-[12px]">
          <p>
            Delete <strong>{deleteFile()}</strong>? This will remove the script and all its automations.
            This cannot be undone.
          </p>
          <Show when={deleteError()}>
            {message => (
              <div class="text-sm text-[var(--error-color)]" role="alert">
                {message()}
              </div>
            )}
          </Show>
        </div>
      </Dialog>
    </div>
  )
}
