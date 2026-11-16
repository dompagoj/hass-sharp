import { vi } from 'vitest'

/** A viewport that evaluates min/max width queries and can resize after mounting. */
export function viewportWidth(initial: number) {
  let width = initial
  const lists: { matches: boolean; media: string; listeners: Set<() => void> }[] = []
  const matches = (query: string) => {
    const constraint = /\((min|max)-width: (\d+)px\)/.exec(query)
    return !!constraint && (constraint[1] === 'max' ? width <= Number(constraint[2]) : width >= Number(constraint[2]))
  }
  vi.stubGlobal('matchMedia', (query: string) => {
    const list = { matches: matches(query), media: query, listeners: new Set<() => void>(),
      addEventListener(_: string, listener: () => void) { this.listeners.add(listener) },
      removeEventListener(_: string, listener: () => void) { this.listeners.delete(listener) } }
    lists.push(list)
    return list
  })
  return (next: number) => {
    width = next
    for (const list of lists) { list.matches = matches(list.media); list.listeners.forEach(listener => listener()) }
  }
}
