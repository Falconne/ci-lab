import { ref, watch } from 'vue'

type ViewMode = 'grid' | 'card'

const STORAGE_KEY = 'mergician-view-mode'

const stored = localStorage.getItem(STORAGE_KEY)
const viewMode = ref<ViewMode>((stored === 'card' || stored === 'grid') ? stored : 'grid')

watch(viewMode, (mode) => {
  localStorage.setItem(STORAGE_KEY, mode)
})

export function useViewMode() {
  return viewMode
}
