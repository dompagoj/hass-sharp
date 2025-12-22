import type { RouteSectionProps } from '@solidjs/router'
import { HassCtx } from '../context'
import { createEffect, useContext } from 'solid-js'
import { Editor } from '../components/editor'

export const AutomationsView = (props: RouteSectionProps) => {
  const hass = useContext(HassCtx)!

  return (
    <div class="h-full">
      <Editor hass={hass} />
    </div>
  )
}
