export interface HassError {
  body: {
    error: string
  }
  error: string
  status_code: number
}
export function errorToHassError(err: Error) {
  return err as unknown as HassError
}

export function findCustomComponent(search: string) {
  // @ts-expect-error
  const components = customElements['h'] as Map<string, any>

  console.log([...components.keys()].filter(c => c.includes(search)).map(name => components.get(name)))
}

/** "1 error", "2 errors": a count with its word in the singular or plural. */
export const plural = (count: number, word: string) => `${count} ${word}${count === 1 ? '' : 's'}`

/** A diagnostics header, "1 error · 2 warnings", counting Monaco-style severities (3 error, 2 warning). */
export const errorWarningCounts = (diagnostics: readonly { severity: number }[]) =>
  `${plural(diagnostics.filter(d => d.severity === 3).length, 'error')} · ${plural(diagnostics.filter(d => d.severity === 2).length, 'warning')}`
