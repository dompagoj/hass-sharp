import { createSignal, type Accessor, type Setter } from 'solid-js'

export function jsonParseOrDefault<T>(raw: string | null | undefined, defaultValue: T) {
  try {
    return JSON.parse(raw ?? '')
  } catch {
    return defaultValue
  }
}

export function useLocalStorage<T>(key: string, defaultValue: T): [Accessor<T>, Setter<T>] {
  const valRaw = localStorage.getItem(key)

  const val = jsonParseOrDefault(valRaw, defaultValue)
  const [getVal, setVal] = createSignal<T>(val)

  const setter = (newVal: T) => {
    const res = setVal(newVal as any)
    localStorage.setItem(key, JSON.stringify(res))
  }

  return [getVal, setter as Setter<T>]
}
