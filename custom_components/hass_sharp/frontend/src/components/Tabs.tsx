import { For } from 'solid-js'

export const Tabs = (props: { id: string; label: string; items: { id: string; label: string }[]; value: string; onChange: (id: string) => void }) => {
  let root!: HTMLDivElement
  return <div ref={root} class="hs-tabs" role="tablist" aria-label={props.label} onKeyDown={event => {
    const current = props.items.findIndex(item => item.id === props.value)
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? props.items.length - 1 : event.key === 'ArrowRight' ? (current + 1) % props.items.length : event.key === 'ArrowLeft' ? (current - 1 + props.items.length) % props.items.length : -1
    if (index < 0) return
    event.preventDefault()
    props.onChange(props.items[index].id)
    root.querySelectorAll<HTMLButtonElement>('[role="tab"]')[index]?.focus()
  }}><For each={props.items}>{item => <button type="button" role="tab" id={`${props.id}-${item.id}`} aria-selected={props.value === item.id}
    aria-controls={`${props.id}-${item.id}-panel`} tabindex={props.value === item.id ? 0 : -1} onClick={() => props.onChange(item.id)}>{item.label}</button>}</For></div>
}
