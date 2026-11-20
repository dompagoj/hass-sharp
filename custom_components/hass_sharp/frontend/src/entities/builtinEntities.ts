import { nodeCatalog } from '../visual/contract'

/** A class of the library the entity help shows, and the files of HassSharp/Devices/ its parts are in, in order. */
export interface BuiltinClass { className: string; files: readonly string[] }
/** One of the entity types an Entity node or a C# entity node has: its domain, the editor's name for it and its class. */
export interface BuiltinEntity extends BuiltinClass { domain: string; label: string }

// Each domain of the catalog's entityDomains, in its order, with the class the library gives it (SharpDomains).
const classes: Record<string, BuiltinClass> = {
  binary_sensor: { className: 'SharpBinarySensor', files: ['SharpEntities.cs'] },
  sensor: { className: 'SharpSensor', files: ['SharpEntities.cs'] },
  switch: { className: 'SharpSwitch', files: ['SharpEntities.cs'] },
  button: { className: 'SharpButton', files: ['SharpControls.cs'] },
  number: { className: 'SharpNumber', files: ['SharpControls.cs'] },
  select: { className: 'SharpSelect', files: ['SharpControls.cs'] },
  text: { className: 'SharpText', files: ['SharpControls.cs'] },
  light: { className: 'SharpLight', files: ['SharpControls.cs'] },
  cover: { className: 'SharpCover', files: ['SharpControls.cs'] },
  camera: { className: 'SharpCamera', files: ['SharpCamera.cs'] },
}

/** The built-in entity types, by the names the editor gives them. */
export const builtinEntities: readonly BuiltinEntity[] = Object.entries(classes).map(([domain, c]) =>
  ({ domain, label: nodeCatalog.entityDomains?.[domain]?.label ?? c.className, ...c }))

/** What the entity types share: setup, start and stop, timers, tracks and services, and the base of a C# device node. */
export const sharedClasses: readonly BuiltinClass[] = [
  { className: 'SharpEntity', files: ['SharpEntity.cs'] },
  { className: 'SharpComponent', files: ['SharpComponent.cs'] },
  { className: 'SharpDevice', files: ['SharpDevice.cs'] },
]
