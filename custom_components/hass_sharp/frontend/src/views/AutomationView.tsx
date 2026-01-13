import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { useQuery, useQueryClient } from '@tanstack/solid-query'
import { createEffect, Match, Switch } from 'solid-js'
import * as monaco from 'monaco-editor'

import { useHass } from '../context'
import type { UserScriptSourceDTO } from '../types'
import { Editor } from '../components/Editor/Editor'
import { errorToHassError } from '../utils'

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
      if (errorToHassError(query.error!).status_code === 404) {
        navigate('/automations')
      }
    }
  })

  const onEditorSave = async (model: monaco.editor.ITextModel) => {
    await hass.callApi('POST', `hass-sharp/automations/${query.data!.fileName}`, { source: model.getValue() })
    await queryClient.invalidateQueries({ queryKey: ['automations', route.params.id], type: 'inactive' })
  }

  return (
    <div class="h-full">
      <Switch>
        <Match when={query.isError}>
          <span>{errorToHassError(query.error!).body.error}</span>
        </Match>
        <Match when={!query.isFetching}>
          <Editor
            fileName={query.data!.fileName}
            initial={query.data!.source!}
            onBackRef="/automations"
            onSave={onEditorSave}
          />
        </Match>
      </Switch>
    </div>
  )
}
