import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { rmSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const outDir = fileURLToPath(new URL('../src/Rta.App/wwwroot', import.meta.url));

// Builds the Hub dashboard into the app's wwwroot (next to the node page in wwwroot/node,
// which must survive, so only our own outputs are cleaned).
export default defineConfig({
  plugins: [
    react(),
    {
      name: 'clean-dashboard-output',
      apply: 'build',
      buildStart() {
        rmSync(resolve(outDir, 'assets'), { recursive: true, force: true });
        rmSync(resolve(outDir, 'index.html'), { force: true });
      },
    },
  ],
  build: { outDir, emptyOutDir: false, assetsDir: 'assets', chunkSizeWarningLimit: 900 },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5180',
      '/ws': { target: 'ws://localhost:5180', ws: true },
    },
  },
});
