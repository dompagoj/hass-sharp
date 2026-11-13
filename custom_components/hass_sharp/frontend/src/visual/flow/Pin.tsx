import { Handle, Position } from '@dschz/solid-flow'
import { useFlow, type PinView } from './flowModel'

/**
 * One connection point. The handle element itself is a zero-size point on the card edge, so every wire attaches
 * to the pin's centre whatever the row height; the pin is drawn by `::after` and `::before` widens the target to
 * 24 px. Pseudo-elements keep the hit test on the handle element, which the canvas library requires.
 */
export function Pin(props: { nodeId: string; pin: PinView; errors?: string[]; describedBy?: string }) {
  const flow = useFlow()
  const type = () => (props.pin.side === 'in' ? 'target' : 'source')
  const state = () => flow.pinState(props.nodeId, props.pin.id, type())
  const errors = () => props.errors ?? []
  const classes = () =>
    [
      'hs-pin',
      `hs-pin--${props.pin.kind}`,
      `hs-pin--${props.pin.side}`,
      props.pin.connected && 'hs-pin--connected',
      props.pin.list && 'hs-pin--list',
      props.pin.error && 'hs-pin--error',
      errors().length && 'hs-pin--invalid',
      state() && `hs-pin--${state()}`,
    ]
      .filter(Boolean)
      .join(' ')
  const activate = () => {
    if (!flow.readOnly()) flow.activateHandle(props.nodeId, props.pin.id, type())
  }
  return (
    <Handle
      type={type()}
      position={props.pin.side === 'in' ? Position.Left : Position.Right}
      id={props.pin.id}
      isConnectable={!flow.readOnly()}
      class={classes()}
      style={{ '--hs-pin-color': props.pin.color }}
      // Read-only pins stay reachable from the keyboard (their name says type and state); they do nothing.
      tabIndex={0}
      aria-label={props.pin.name}
      aria-invalid={errors().length > 0 || undefined}
      aria-describedby={errors().length ? props.describedBy : undefined}
      title={errors().join('; ') || [props.pin.name, props.pin.description].filter(Boolean).join('. ')}
      data-type={props.pin.typeId}
      on:click={(event: MouseEvent) => {
        event.stopPropagation()
        if (!flow.moved()) activate()
      }}
      on:keydown={(event: KeyboardEvent) => {
        if (event.key !== 'Enter' && event.key !== ' ') return
        event.preventDefault()
        event.stopPropagation()
        activate()
      }}
    />
  )
}
