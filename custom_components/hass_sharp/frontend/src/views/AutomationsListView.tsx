import type { RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { useContext, For, Suspense } from 'solid-js'
import type { UserScriptDTO } from '../types'
import { useQuery } from '@tanstack/solid-query'

export const AutomationsListView = (_props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!

  const query = useQuery(() => ({
    queryKey: ['automations'],
    queryFn: () => hass.callApi<UserScriptDTO[]>('GET', 'hass-sharp/automations'),
  }))

  return (
    <div class="w-full overflow-y-auto p-4">
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
                </ha-md-list-item>
                <div class="flex flex-col">
                  {file.classes.map((klass, idx) => (
                    <ha-md-list-item type="button" href={`/hass-sharp/automations/${file.fileName}`}>
                      <div slot="headline">{klass.name.split('.').pop()}</div>
                      <div slot="supporting-text" class="text-xs text-gray-400">
                        {klass.methods.length} methods
                      </div>
                      <ha-icon slot="start" icon="mdi:code-braces" class="ml-4 opacity-70"></ha-icon>
                      <ha-icon-button slot="end">
                        <ha-icon icon="mdi:chevron-right"></ha-icon>
                      </ha-icon-button>
                      {idx < file.classes.length - 1 && (
                        <div slot="bottom" class="border-b border-gray-800 ml-16"></div>
                      )}
                    </ha-md-list-item>
                  ))}
                </div>
              </>
            )}
          </For>
        </Suspense>
      </ha-md-list>
    </div>
  )
}
