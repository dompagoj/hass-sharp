import { useNavigate, type RouteSectionProps } from '@solidjs/router'

export const MainLayout = (props: RouteSectionProps) => {
  const navigate = useNavigate()

  return (
    <>
      <div class="header">
        <div class="p-4">
          <span class="text-3xl ml-6">Hass Sharp</span>
        </div>
        <ha-tab-group>
          <ha-tab-group-tab class="text-lg" onClick={() => navigate('/automations')}>
            Automations
          </ha-tab-group-tab>
          <ha-tab-group-tab class="text-lg" onClick={() => navigate('/devices')}>
            Devices
          </ha-tab-group-tab>
          <ha-tab-group-tab class="text-lg" onClick={() => navigate('/entities')}>
            Entities
          </ha-tab-group-tab>
        </ha-tab-group>
      </div>
      <div class="header-content">{props.children}</div>
    </>
  )
}
