import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
// The .js extension is required: the config is type-checked under nodenext module
// resolution, which demands explicit extensions on relative ESM imports.
import { mockApi } from './dev/mockApi.js'

// https://vite.dev/config/
export default defineConfig({
  // mockApi is registered with apply: 'serve', so it exists only under `npm run dev` and
  // cannot reach a production build. In the shipped app the API is served by the WPF shell
  // intercepting WebView2 resource requests; without this, `npm run dev` has no backend and
  // the UI cannot be reviewed in a browser at all.
  plugins: [react(), mockApi()],

  // Assets are served from a virtual host in the shell, not from a web server root.
  // Relative base keeps asset URLs correct there.
  base: './',

  build: {
    // The shell copies this directory into its output and serves it.
    outDir: 'dist',
    emptyOutDir: true,
    // Source maps only in dev; shipping them would bloat the installer for no user benefit.
    sourcemap: false,
  },

  server: {
    port: 5173,
    strictPort: true,
  },
})
