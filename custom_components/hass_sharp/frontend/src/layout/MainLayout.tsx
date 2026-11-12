import { A, type RouteSectionProps } from '@solidjs/router'
import { Icon, Button } from '../components/Controls'

export const MainLayout = (props: RouteSectionProps) => {
  let main!: HTMLElement
  const toggleSidebar = (event: MouseEvent) => event.currentTarget?.dispatchEvent(new CustomEvent('hass-toggle-menu', { bubbles: true, composed: true }))
  return <div class="hs-app">
    <a href="#hs-main" class="hs-skip-link" onClick={event => { event.preventDefault(); main.focus() }}>Skip to content</a>
    <header class="hs-app-header">
      <Button variant="ghost" class="hs-icon-button hs-sidebar-toggle" aria-label="Toggle Home Assistant sidebar" onClick={toggleSidebar}><Icon name="menu" /></Button>
      <A href="/automations" class="hs-brand" aria-label="HassSharp home"><span class="hs-brand-mark"><Icon name="code" /></span><span>Hass<span class="hs-brand-accent">Sharp</span></span></A>
      <nav aria-label="HassSharp"><A href="/automations" class="hs-nav-link" activeClass="hs-nav-link--active">Automations</A><A href="/libraries" class="hs-nav-link" activeClass="hs-nav-link--active">Node library</A></nav>
      <span class="hs-workspace-label">HOME ASSISTANT WORKSPACE</span>
    </header>
    <main ref={main} id="hs-main" tabindex="-1" class="hs-main">{props.children}</main>
  </div>
}
