import type { HomeAssistant } from './types'
import { Route, Navigate, Router } from '@solidjs/router'
import { TODOView } from './views/TODOView'
import { MainLayout } from './layout/MainLayout'
import { HassCtx } from './context'
import { AutomationsView } from './views/AutomationsView'

function App(props: { hass: HomeAssistant }) {
  return (
    <HassCtx.Provider value={props.hass}>
      <Router base="/hass-sharp" root={MainLayout}>
        <Route path="/" component={() => <Navigate href="/automations" />} />
        <Route path="/automations" component={AutomationsView} />
        <Route path="/devices" component={TODOView} />
        <Route path="/entities" component={TODOView} />
      </Router>
    </HassCtx.Provider>
  )
}

export default App
