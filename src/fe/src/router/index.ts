import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '@/views/HomeView.vue'
import MergeGroupDetailsView from '@/views/MergeGroupDetailsView.vue'
import QueuesView from '@/views/QueuesView.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      component: HomeView,
      meta: { title: 'Dashboard' },
    },
    {
      path: '/merge-group/:mergeGroupId',
      name: 'merge-group-details',
      component: MergeGroupDetailsView,
      meta: { title: 'Merge Group' },
    },
    {
      path: '/queues',
      name: 'queues',
      component: QueuesView,
      meta: { title: 'Queues' },
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      redirect: '/',
    },
  ],
})

router.afterEach((to) => {
  const queryTitle = to.query?.title as string | undefined
  const metaTitle = to.meta?.title as string | undefined
  const title = queryTitle || metaTitle
  document.title = title ? `${title} — Mergician` : 'Mergician'
})

export default router
