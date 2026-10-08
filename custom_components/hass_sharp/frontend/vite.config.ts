import tailwindcss from '@tailwindcss/vite'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import solidPlugin from 'vite-plugin-solid'

const __dirname = dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [solidPlugin(), tailwindcss()],
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
