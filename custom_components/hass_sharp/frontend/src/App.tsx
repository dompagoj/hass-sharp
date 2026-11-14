import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Route, Navigate, Router } from '@solidjs/router'

import type { HomeAssistant } from './types'
import { TODOView } from './views/TODOView'
import { MainLayout } from './layout/MainLayout'
import { HassCtx } from './context'
import { AutomationsListView } from './views/AutomationsListView'
import { VisualAutomationView } from './views/VisualAutomationView'
import { RunTraceView } from './views/RunTraceView'
import { findCustomComponent } from './utils'
import { lazyView } from './components/LazyLoad'

// Monaco comes with the node library editor (and a Custom C# node's code editor): never in the first paint. A failed
// load says so, with Try again.
export const NodeLibraryView = lazyView(() => import('./views/NodeLibraryView').then(module => module.NodeLibraryView), 'node libraries')

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

window.findCustomComponent = findCustomComponent

function App(props: { hass: HomeAssistant }) {
  return (
    <QueryClientProvider client={client}>
      <HassCtx.Provider value={props.hass}>
        <Router base="/hass-sharp" root={MainLayout}>
          <Route path="/" component={() => <Navigate href="/automations" />} />
          <Route path="/automations" component={AutomationsListView} />
          <Route path="/visual/:id" component={VisualAutomationView} />
          <Route path="/visual/:id/runs/:runId" component={RunTraceView} />
          <Route path="/libraries" component={NodeLibraryView} />
          <Route path="/devices" component={TODOView} />
          <Route path="/entities" component={TODOView} />
        </Router>
      </HassCtx.Provider>
    </QueryClientProvider>
  )
}

export default App
