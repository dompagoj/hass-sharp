import type { LitElement } from 'lit'
import type { JSX } from 'solid-js'

import { HassValueChangedEvent } from './types'

type HA<T = any> = JSX.HTMLAttributes<any> & {
  'on:value-changed'?: (e: HassValueChangedEvent) => any
} & T

declare module 'solid-js' {
  namespace JSX {
    interface IntrinsicElements {
      'ha-progress-button': HaProgressButton
      'ha-md-list': HA
      'ha-md-list-item': HA
      'ha-icon': HA
      'ha-icon-button': HA
      'ha-tab-group': HA
      'ha-tab-group-tab': HA
      'ha-card': HA
      'ha-button': HA
      'ha-dialog': HA<{
        open?: boolean
        'prop:open'?: boolean
        'prop:preventScrimClose'?: boolean
        width?: 'small' | 'medium' | 'large' | 'full'
        'header-title'?: string
        'on:closed'?: () => void
      }>
      'ha-dialog-footer': HA
      'ha-spinner': HA
      'ha-switch': HA<{ checked: boolean }>
      'ha-icon-button-arrow-prev': HA
      'ha-combo-box': HA<{ items?: any[]; value?: any; label?: string; hideClearIcon?: boolean }>
    }
  }
}

export declare type HaProgressButton = {
  disabled?: boolean
  progress?: boolean
  raised?: boolean
} & HA

declare global {
  interface Window {
    findCustomComponent: typeof import('./utils').findCustomComponent
  }
}
