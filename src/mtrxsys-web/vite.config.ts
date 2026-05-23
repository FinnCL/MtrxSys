import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: 5173,
    strictPort: true,
    watch: {
      usePolling: process.env.CHOKIDAR_USEPOLLING === 'true',
      interval: 300,
    },
    hmr: {
      host: 'localhost',
      port: 5173,
      protocol: 'ws',
    },
  },
})
