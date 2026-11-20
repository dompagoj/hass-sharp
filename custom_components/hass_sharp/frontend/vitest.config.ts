import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { searchForWorkspaceRoot } from 'vite'
import { defineConfig } from 'vitest/config'
import solid from 'vite-plugin-solid'

const __dirname = dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [solid()],
  // The library's entity classes, which the entity help imports as text (as vite.config.ts allows them).
  server: { fs: { allow: [searchForWorkspaceRoot(__dirname), resolve(__dirname, '../HassSharp/Devices')] } },
  test: { environment: 'happy-dom', include: ['tests/**/*.test.{ts,tsx}'], setupFiles: ['tests/setup.ts'] },
})
