import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The API is same-origin in production (served behind the same ingress); in dev we proxy it so no CORS is needed.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': { target: process.env.DAM_API_URL ?? 'http://localhost:5080', changeOrigin: false } },
  },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test-setup.ts'], css: false },
});
