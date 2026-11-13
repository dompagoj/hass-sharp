import type { Component } from 'solid-js'
import { Dynamic } from 'solid-js/web'
import { ColorWidget, ConstantWidget, DateTimeWidget, DurationWidget, JsonWidget, NumberWidget, SelectWidget, TextWidget, ToggleWidget } from './basic'
import type { WidgetProps } from './common'
import type { WidgetKind } from './model'
import { EntityWidget, RegistryWidget, StateWidget, TargetWidget } from './pickers'

const components: Record<Exclude<WidgetKind, 'none'>, Component<WidgetProps>> = {
  toggle: ToggleWidget, number: NumberWidget, text: TextWidget, select: SelectWidget, constant: ConstantWidget,
  entity: EntityWidget, state: StateWidget, device: RegistryWidget, area: RegistryWidget, floor: RegistryWidget, label: RegistryWidget,
  target: TargetWidget, duration: DurationWidget, time: DateTimeWidget, date: DateTimeWidget, datetime: DateTimeWidget,
  color: ColorWidget, json: JsonWidget,
}
/** The control for `props.widget`; a wire-only input (`none`) has no control. */
export function WidgetControl(props: WidgetProps) {
  return <Dynamic component={props.widget.kind === 'none' ? undefined : components[props.widget.kind]} {...props} />
}
