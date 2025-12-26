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
