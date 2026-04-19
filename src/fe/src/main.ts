import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import { createVuetify } from 'vuetify'
import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'

// Handle stale chunk load failures after a deployment.
// When Vite-built assets are replaced, the browser may try to fetch old chunk
// filenames that no longer exist. This listener catches those failures and
// forces a clean page reload so the user gets the new version automatically.
window.addEventListener('vite:preloadError', (event) => {
  const lastReload = sessionStorage.getItem('mergician-chunk-reload')
  const now = Date.now()
  if (lastReload && now - parseInt(lastReload, 10) < 10_000) {
    console.error('[Mergician] Chunk preload failed after recent reload — not reloading again', event)
    return
  }
  console.warn('[Mergician] Chunk preload failed — reloading to pick up new version', event)
  sessionStorage.setItem('mergician-chunk-reload', now.toString())
  window.location.reload()
})

const vuetify = createVuetify({
  theme: {
    defaultTheme: 'light',
    themes: {
      light: {
        colors: {
          primary: '#1867C0',
          secondary: '#5CBBF6',
        },
      },
    },
  },
})

const app = createApp(App)
app.use(router)
app.use(vuetify)
app.mount('#app')
