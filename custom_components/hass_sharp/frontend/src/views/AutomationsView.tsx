import type { RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { createEffect, createSignal, useContext } from 'solid-js'
import type { UserFile } from '../types'

export const AutomationsView = (props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!
  const [userFiles, setUserFiles] = createSignal<UserFile[]>([])

  createEffect(async () => {
    try {
      const files = await hass.callApi<UserFile[]>('GET', 'hass-sharp/automations')
      setUserFiles(files)
    } catch (e) {
      console.error('Failed to get user files', e)
    }
  })

  return (
    <div class="flex h-full">
      <div class="w-64 border-r border-gray-700 bg-[#1e1e1e] p-4 text-white overflow-y-auto">
        <h2 class="mb-4 text-xl font-bold">Files</h2>
        <ul>
          {userFiles().map(file => (
            <li class="mb-4">
              <div class="flex items-center gap-2 font-medium text-blue-400">
                <span class="mdi mdi-file-code-outline"></span>
                {file.name}
              </div>
              <ul class="ml-6 mt-1 space-y-1">
                {file.scripts.map(script => (
                  <li class="flex items-center gap-2 text-sm text-gray-400">
                    <span class="mdi mdi-class"></span>
                    {script.className.split('.').pop()}
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ul>
      </div>
      {/* <div class="flex-1 min-w-0">
        <Editor hass={hass} />
      </div> */}
    </div>
  )
}
