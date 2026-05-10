import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      redirect: '/dashboard',
    },
    {
      path: '/dashboard',
      name: 'Dashboard',
      component: () => import('@/views/DashboardView.vue'),
      meta: { title: '报警看板' },
    },
    {
      path: '/alarms',
      name: 'Alarms',
      component: () => import('@/views/AlarmsView.vue'),
      meta: { title: '报警管理' },
    },
    {
      path: '/alarms/:id',
      name: 'AlarmDetail',
      component: () => import('@/views/AlarmDetailView.vue'),
      meta: { title: '报警详情' },
    },
    {
      path: '/rules',
      name: 'Rules',
      component: () => import('@/views/RulesView.vue'),
      meta: { title: '规则管理' },
    },
    {
      path: '/rules/:id',
      name: 'RuleEdit',
      component: () => import('@/views/RuleEditView.vue'),
      meta: { title: '规则编辑' },
    },
    {
      path: '/devices',
      name: 'Devices',
      component: () => import('@/views/DevicesView.vue'),
      meta: { title: '设备状态' },
    },
  ],
})

router.beforeEach((to) => {
  document.title = `${to.meta.title || 'AmGateway'} - AmGateway`
})

export default router
