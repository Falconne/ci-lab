import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'
import vuetify from 'vite-plugin-vuetify'
import { fileURLToPath, URL } from 'node:url'
import { randomUUID } from 'node:crypto'
import { execSync } from 'node:child_process'

// Get git hash from environment (set during Docker build) or from git directly (for dev builds).
// Falls back to a random UUID if git is not available.
function getVersion(): string {
  // First try environment variable set during Docker build
  if (process.env.VITE_GIT_HASH) {
    return process.env.VITE_GIT_HASH
  }

  // Try to get from git directly (for local development)
  try {
    return execSync('git rev-parse HEAD').toString().trim()
  } catch {
    // Fallback to random UUID if git is not available
    return randomUUID().replace(/-/g, '').slice(0, 12)
  }
}

const buildHash = getVersion()

// Generates a version.json file at build time with the build hash.
// The running app periodically fetches this to detect when a new version is deployed.
function versionJsonPlugin(): Plugin {
  return {
    name: 'version-json',
    apply: 'build',
    generateBundle() {
      this.emitFile({
        type: 'asset',
        fileName: 'version.json',
        source: JSON.stringify({ version: buildHash }),
      })
    },
  }
}

export default defineConfig({
  define: {
    __APP_VERSION__: JSON.stringify(buildHash),
  },
  plugins: [
    vue(),
    vuetify({
      autoImport: true, // Enables tree shaking for Vuetify components
    }),
    versionJsonPlugin(),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
  build: {
    // Tree shaking is enabled by default in production builds
    rollupOptions: {
      output: {
        manualChunks: {
          vuetify: ['vuetify'],
          vendor: ['vue', 'vue-router'],
        },
      },
    },
  },
})
