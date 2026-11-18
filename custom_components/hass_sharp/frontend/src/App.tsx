import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { Route, Navigate, Router } from '@solidjs/router'

import type { HomeAssistant } from './types'
import { MainLayout } from './layout/MainLayout'
import { HassCtx } from './context'
import { AutomationsListView } from './views/AutomationsListView'
import { VisualAutomationView } from './views/VisualAutomationView'
import { EntityRunTraceView, RunTraceView } from './views/RunTraceView'
import { EntityDefinitionView } from './views/EntityDefinitionView'
import { EntitiesListView } from './views/EntitiesListView'
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

/** The panel's pages, under the router's base. Devices are part of the Entities section. */
export function AppRoutes() {
  return <>
    <Route path="/" component={() => <Navigate href="/automations" />} />
    <Route path="/automations" component={AutomationsListView} />
    <Route path="/visual/:id" component={VisualAutomationView} />
    <Route path="/visual/:id/runs/:runId" component={RunTraceView} />
    <Route path="/libraries" component={NodeLibraryView} />
    <Route path="/devices" component={() => <Navigate href="/entities" />} />
    <Route path="/entities" component={EntitiesListView} />
    <Route path="/entities/:id" component={EntityDefinitionView} />
    <Route path="/entities/:id/runs/:runId" component={EntityRunTraceView} />
  </>
}

function App(props: { hass: HomeAssistant }) {
  return (
    <QueryClientProvider client={client}>
      <HassCtx.Provider value={props.hass}>
        <Router base="/hass-sharp" root={MainLayout}>
          <AppRoutes />
        </Router>
      </HassCtx.Provider>
    </QueryClientProvider>
  )
}

export default App
