import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: { license: { fileName: 'third-party-licenses.txt' } },
  server: {
    port: 5199,
    strictPort: true,
    proxy: { '/api': 'http://localhost:5299' },
  },
})
