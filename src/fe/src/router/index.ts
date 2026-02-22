import { createRouter, createWebHistory } from 'vue-router'
import HomeView from '@/views/HomeView.vue'
import MergeGroupDetailsView from '@/views/MergeGroupDetailsView.vue'

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
  ],
})

export default router
