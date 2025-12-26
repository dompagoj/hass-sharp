import { createContext, useContext } from 'solid-js'
import type { HomeAssistant } from './types'

export const HassCtx = createContext<HomeAssistant>()

export const useHass = () => useContext(HassCtx)!
