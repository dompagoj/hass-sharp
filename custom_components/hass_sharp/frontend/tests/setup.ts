import { beforeEach } from 'vitest'

// The editor keeps drafts of unsaved changes in localStorage, and a disposed editor writes the draft of what it left
// unsaved: each test starts with none, so no test is offered another's.
beforeEach(() => {
  try {
    localStorage.clear()
  } catch {
    // No storage in this environment.
  }
})
