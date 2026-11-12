import { createUniqueId, splitProps, type JSX } from 'solid-js'

interface ButtonProps extends JSX.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger'
  busy?: boolean
}
export const Button = (props: ButtonProps) => {
  const [local, rest] = splitProps(props, ['variant', 'busy', 'class', 'children', 'disabled'])
  return <button {...rest} type={rest.type ?? 'button'} class={`hs-button hs-button--${local.variant ?? 'secondary'} ${local.class ?? ''}`}
    disabled={local.disabled || local.busy} aria-busy={local.busy || undefined}>{local.children}</button>
}

interface FieldProps extends JSX.InputHTMLAttributes<HTMLInputElement> {
  label: string
  hint?: string
}
export const Field = (props: FieldProps) => {
  const [local, rest] = splitProps(props, ['label', 'hint', 'class', 'id'])
  const id = local.id ?? createUniqueId()
  return <div class={`hs-field ${local.class ?? ''}`}>
    <label for={id}>{local.label}</label>
    <input {...rest} id={id} class="hs-input" aria-describedby={local.hint ? `${id}-hint` : undefined} />
    {local.hint && <span id={`${id}-hint`} class="hs-field-hint">{local.hint}</span>}
  </div>
}

export const Status = (props: { children: JSX.Element; tone?: 'neutral' | 'success' | 'danger' }) =>
  <span class={`hs-status hs-status--${props.tone ?? 'neutral'}`}>{props.children}</span>

const paths = {
  code: 'm8 7-5 5 5 5m8-10 5 5-5 5m-3-14-2 18',
  plus: 'M12 5v14M5 12h14',
  arrow: 'm12 5-7 7 7 7M5 12h14',
  more: 'M5 11v2m7-2v2m7-2v2',
  close: 'm6 6 12 12M6 18 18 6',
  search: 'm21 21-5-5M18 10a8 8 0 1 1-16 0 8 8 0 0 1 16 0',
  save: 'M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h12l4 4v12a2 2 0 0 1-2 2ZM7 3v6h10V3M7 21v-8h10v8',
  chevron: 'm9 5 7 7-7 7',
  menu: 'M3 6h18M3 12h18M3 18h18',
}
export const Icon = (props: { name: keyof typeof paths }) => <svg class="hs-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d={paths[props.name]} /></svg>
