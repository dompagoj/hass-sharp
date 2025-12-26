import type { LitElement } from 'lit'
import type { JSX } from 'solid-js'

declare module 'solid-js' {
  namespace JSX {
    interface IntrinsicElements {
      'ha-progress-button': HaProgressButton
      'ha-md-list': JSX.HTMLAttributes<any>
      'ha-md-list-item': JSX.HTMLAttributes<any>
      'ha-icon': JSX.HTMLAttributes<any>
      'ha-icon-button': JSX.HTMLAttributes<any>
      'ha-tab-group': JSX.HTMLAttributes<any>
      'ha-tab-group-tab': JSX.HTMLAttributes<any>
      'ha-card': JSX.HTMLAttributes<any>
      'ha-button': JSX.HTMLAttributes<any>
      'ha-spinner': JSX.HTMLAttributes<any>
    }
  }
}

export declare type HaProgressButton = {
  disabled?: boolean
  progress?: boolean
  raised?: boolean
} & JSX.HTMLAttributes<any>
