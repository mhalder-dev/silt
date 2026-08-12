import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],

  // Assets are served from a WebView2 virtual host mapping (https://silt.invalid/),
  // not from a web server root. Relative base keeps asset URLs correct there.
  base: './',

  build: {
    // The shell copies this directory into its output and maps it as the virtual host.
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
