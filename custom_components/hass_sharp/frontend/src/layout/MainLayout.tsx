import { A, type RouteSectionProps } from '@solidjs/router'

export const MainLayout = (props: RouteSectionProps) => {
  return (
    <>
      <div class="header">
        <div class="p-4">
          <span class="text-3xl ml-6">Hass Sharp</span>
        </div>
        <ha-tab-group class="tab-group">
          <ha-tab-group-tab class="text-lg">
            <A href="/automations">Automations</A>
          </ha-tab-group-tab>
          <ha-tab-group-tab class="text-lg">
            <A href="/devices">Devices</A>
          </ha-tab-group-tab>
          <ha-tab-group-tab class="text-lg">
            <A href="/entities">Entities</A>
          </ha-tab-group-tab>
        </ha-tab-group>
      </div>
      <div class="header-content">{props.children}</div>
    </>
  )
}
