import tailwindcss from '@tailwindcss/vite'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, searchForWorkspaceRoot } from 'vite'
import solidPlugin from 'vite-plugin-solid'

const __dirname = dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  base: '/hass-sharp-static/',
  plugins: [solidPlugin(), tailwindcss()],
  // The entity help imports the library's entity classes as text (`src/entities/builtinSource.ts`, `?raw`): the dev
  // server serves their folder beside the frontend's own, as vitest.config.ts does for the tests. The build reads it anyway.
  server: { fs: { allow: [searchForWorkspaceRoot(__dirname), resolve(__dirname, '../HassSharp/Devices')] } },
  resolve: {
    alias: [
      {
        find: /^monaco-editor\/esm\/vs\/editor\/editor\.api$/,
        replacement: 'monaco-editor/editor/editor.api',
      },
    ],
  },
  build: {
    target: 'esnext',
    outDir: '../www',
    lib: {
      entry: resolve(__dirname, 'src/hass-sharp.ts'),
      formats: ['es'],
    },
    minify: false,
    rolldownOptions: {
      external: ['lit'],
      output: {
        entryFileNames: 'hass-sharp.js',
        assetFileNames: 'hass-sharp.[ext]',
        paths: {
          lit: 'https://unpkg.com/lit@3.3.3/index.js?module',
        },
      },
    },
  },
})
