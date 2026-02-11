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
  console.warn('[Mergician] Chunk preload failed — reloading to pick up new version', event)
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
