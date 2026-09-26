import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build the React SPA into ../wwwroot — embedded into the .NET single-file exe.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    target: 'es2020',
    chunkSizeWarningLimit: 1200,
  },
});
