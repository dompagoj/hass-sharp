// Build the mock-data fixture for an existing local HA static route.
import { defineConfig } from 'vite'
import solid from 'vite-plugin-solid'
import tailwindcss from '@tailwindcss/vite'
export default defineConfig({
  base: '/hass-sharp-static/visual-check-6c76542/',
  plugins: [solid(), tailwindcss()],
  resolve: { alias: [{ find: /^monaco-editor\/esm\/vs\/editor\/editor\.api$/, replacement: 'monaco-editor/editor/editor.api' }] },
  build: {
    outDir: '../www/visual-check-6c76542',
    emptyOutDir: true,
    rolldownOptions: { input: ['tests/preview-app.html', 'tests/visual-check.html'] },
  },
})
