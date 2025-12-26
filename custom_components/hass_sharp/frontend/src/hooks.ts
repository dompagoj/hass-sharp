import { createSignal, type Accessor, type Setter } from 'solid-js'

export function useLocalStorage<T>(key: string, defaultValue: T): [Accessor<T>, Setter<T>] {
  const valRaw = localStorage.getItem(key)
  const val = valRaw ? (JSON.parse(valRaw ?? '') as T) : defaultValue
  const [getVal, setVal] = createSignal<T>(val)

  const setter = (newVal: T) => {
    localStorage.setItem(key, JSON.stringify(newVal))
    setVal(newVal as any)
  }

  return [getVal, setter as Setter<T>]
}
