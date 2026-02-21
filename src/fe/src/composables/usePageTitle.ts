import { ref } from 'vue'

// Module-level singleton so all components share the same title state
const pageTitle = ref('')

export function usePageTitle() {
  function setPageTitle(title: string) {
    pageTitle.value = title
  }

  return { pageTitle, setPageTitle }
}
