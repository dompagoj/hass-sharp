import { createContext } from 'solid-js'
import type { HomeAssistant } from './types'

export const HassCtx = createContext<HomeAssistant>()
