import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Route, Navigate, Router } from '@solidjs/router'

import type { HomeAssistant } from './types'
import { TODOView } from './views/TODOView'
import { MainLayout } from './layout/MainLayout'
import { HassCtx } from './context'
import { AutomationsListView } from './views/AutomationsListView'
import { AutomationView } from './views/AutomationView'

const client = new QueryClient({
  defaultOptions: {
    queries: {
      refetchOnWindowFocus: false,
      retry: false,
      staleTime: 1000 * 60 * 5,
    },
  },
})

// @ts-expect-error
window.process = {
  env: {
    NODE_ENV: import.meta.env,
  },
}
console.log(import.meta.env.PROD)

function App(props: { hass: HomeAssistant }) {
  return (
    <QueryClientProvider client={client}>
      <HassCtx.Provider value={props.hass}>
        <Router base="/hass-sharp" root={MainLayout}>
          <Route path="/" component={() => <Navigate href="/automations" />} />
          <Route path="/automations" component={AutomationsListView} />
          <Route path="/automations/:id" component={AutomationView} />
          <Route path="/devices" component={TODOView} />
          <Route path="/entities" component={TODOView} />
        </Router>
      </HassCtx.Provider>
    </QueryClientProvider>
  )
}

export default App
