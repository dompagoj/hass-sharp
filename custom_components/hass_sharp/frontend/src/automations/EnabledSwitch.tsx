/**
 * The automation's on/off switch, named "<name> on" for screen readers, with "On" or "Off" beside it. The checkbox
 * shows `enabled` only: a change asks the owner, which updates `enabled` (optimistically) or leaves it as it is.
 * While its own request is in flight it is `busy`: it keeps focus (it is not disabled) and ignores further presses.
 */
export function EnabledSwitch(props: { name: string; enabled: boolean; disabled?: boolean; busy?: boolean; class?: string; onChange(enabled: boolean): void }) {
  return (
    <label class={`hs-enabled ${props.class ?? ''}`} title={props.enabled ? 'On: its triggers start it' : 'Off: nothing starts it by itself; Run now still works'}>
      <input
        type="checkbox"
        role="switch"
        class="hs-switch"
        aria-label={`${props.name} on`}
        checked={props.enabled}
        disabled={props.disabled}
        aria-busy={props.busy ? 'true' : undefined}
        onChange={event => {
          const wanted = event.currentTarget.checked
          event.currentTarget.checked = props.enabled
          if (!props.busy) props.onChange(wanted)
        }}
      />
      <span class="hs-enabled__text" aria-hidden="true">{props.enabled ? 'On' : 'Off'}</span>
    </label>
  )
}
