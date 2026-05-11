import { createRouter, createWebHistory } from 'vue-router'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'Login',
      component: () => import('@/views/LoginView.vue'),
      meta: { title: '登录', public: true },
    },
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
    {
      path: '/workorders',
      name: 'WorkOrders',
      component: () => import('@/views/WorkOrdersView.vue'),
      meta: { title: '维修工单' },
    },
  ],
})

// 路由守卫：未登录跳转登录页
router.beforeEach((to) => {
  document.title = `${to.meta.title || 'AmGateway'} - AmGateway`

  const token = localStorage.getItem('amgateway_token')
  const isPublic = to.meta.public as boolean

  if (!token && !isPublic) {
    return { name: 'Login', query: { redirect: to.fullPath } }
  }

  // 已登录访问登录页，跳转首页
  if (token && to.name === 'Login') {
    return { name: 'Dashboard' }
  }
})

export default router
