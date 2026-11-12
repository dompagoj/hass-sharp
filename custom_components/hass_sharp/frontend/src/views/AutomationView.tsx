import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { createEffect, Show } from 'solid-js'
import * as monaco from 'monaco-editor'

import { useHass } from '../context'
import type { UserScriptSourceDTO } from '../types'
import { Editor } from '../components/Editor/Editor'
import { errorToHassError } from '../utils'
import { Button } from '../components/Controls'

export const AutomationView = (route: RouteSectionProps) => {
  const hass = useHass()
  const navigate = useNavigate()

  const queryClient = useQueryClient()

  const query = useQuery(() => ({
    queryKey: ['automations', route.params.id],
    queryFn: () =>
      hass.callApi<UserScriptSourceDTO>(
        'GET',
        `hass-sharp/automations/${encodeURIComponent(route.params.id as string)}`,
      ),
  }))

  createEffect(() => {
    if (query.isError) {
      if (!query.data && errorToHassError(query.error!).status_code === 404) {
        navigate('/automations')
      }
    }
  })

  const onEditorSave = async (fileName: string, model: monaco.editor.ITextModel) => {
    await hass.callApi('POST', `hass-sharp/automations/${encodeURIComponent(fileName)}`, { source: model.getValue() })
    await queryClient.invalidateQueries({ queryKey: ['automations', fileName], type: 'inactive' })
  }

  return (
    <div class="hs-detail">
      <Show when={query.isPending}><div class="hs-empty" role="status">Loading automation…</div></Show>
      <Show when={query.isError}>
        <div class="hs-notice hs-notice--danger" role="alert"><h2>{query.data ? 'Automation couldn’t refresh' : 'Automation couldn’t load'}</h2>
          <p>{errorToHassError(query.error!).body?.error ?? 'Check your connection and try again.'}</p>
          <Button onClick={() => void query.refetch()}>Try again</Button>
        </div>
      </Show>
      <Show when={query.data?.fileName} keyed>{fileName =>
        <Editor fileName={fileName} initial={query.data!.source} onBackRef="/automations" onSave={model => onEditorSave(fileName, model)} />
      }</Show>
    </div>
  )
}
