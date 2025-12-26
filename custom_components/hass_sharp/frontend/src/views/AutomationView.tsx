import { useNavigate, type RouteSectionProps } from '@solidjs/router'
import { useQuery } from '@tanstack/solid-query'
import { createEffect, Match, Switch } from 'solid-js'

import { useHass } from '../context'
import type { UserScriptSourceDTO } from '../types'
import { Editor } from '../components/Editor'
import { errorToHassError } from '../utils'

export const AutomationView = (route: RouteSectionProps) => {
  const hass = useHass()
  const navigate = useNavigate()

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

  return (
    <div class="h-full">
      <Switch>
        <Match when={query.isError}>
          <span>{errorToHassError(query.error!).body.error}</span>
        </Match>
        <Match when={!query.isFetching}>
          <Editor initial={query.data?.source!} />
        </Match>
      </Switch>
    </div>
  )
}
