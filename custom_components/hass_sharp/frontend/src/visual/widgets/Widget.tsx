import type { Component } from 'solid-js'
import { Dynamic } from 'solid-js/web'
import { ColorWidget, ConstantWidget, DateTimeWidget, DurationWidget, JsonWidget, MixedWidget, NumberWidget, SelectWidget, TextWidget, ToggleWidget } from './basic'
import type { WidgetProps } from './common'
import type { WidgetKind } from './model'
import { EntityWidget, RegistryWidget, StateWidget, TargetWidget } from './pickers'

const components: Record<Exclude<WidgetKind, 'none'>, Component<WidgetProps>> = {
  toggle: ToggleWidget, number: NumberWidget, text: TextWidget, select: SelectWidget, constant: ConstantWidget,
  entity: EntityWidget, state: StateWidget, device: RegistryWidget, area: RegistryWidget, floor: RegistryWidget, label: RegistryWidget,
  target: TargetWidget, duration: DurationWidget, time: DateTimeWidget, date: DateTimeWidget, datetime: DateTimeWidget,
  color: ColorWidget, mixed: MixedWidget, json: JsonWidget,
}
/** The control for `props.widget`; an input that can only be connected (`none`) has no control. */
export function WidgetControl(props: WidgetProps) {
  return <Dynamic component={props.widget.kind === 'none' ? undefined : components[props.widget.kind]} {...props} />
}
