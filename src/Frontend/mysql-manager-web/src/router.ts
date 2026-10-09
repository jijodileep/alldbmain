import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import AdminLayout from './layouts/AdminLayout.vue'
import LoginView from './views/LoginView.vue'
import DashboardView from './views/DashboardView.vue'
import ServersView from './views/ServersView.vue'
import ServerFormView from './views/ServerFormView.vue'
import ServerDetailView from './views/ServerDetailView.vue'
import DatabasesView from './views/DatabasesView.vue'
import DatabaseDetailView from './views/DatabaseDetailView.vue'
import BackupsView from './views/BackupsView.vue'
import BackupDetailView from './views/BackupDetailView.vue'
import SchedulesView from './views/SchedulesView.vue'
import ScheduleFormView from './views/ScheduleFormView.vue'
import MigrationsView from './views/MigrationsView.vue'
import MigrationCreateView from './views/MigrationCreateView.vue'
import MigrationDetailView from './views/MigrationDetailView.vue'
import RestoresView from './views/RestoresView.vue'
import GitView from './views/GitView.vue'
import JobsView from './views/JobsView.vue'
import JobDetailView from './views/JobDetailView.vue'
import AuditView from './views/AuditView.vue'
import NotificationsView from './views/NotificationsView.vue'
import SettingsView from './views/SettingsView.vue'
import UsersView from './views/UsersView.vue'
import RolesView from './views/RolesView.vue'
import HealthView from './views/HealthView.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: LoginView },
    {
      path: '/',
      component: AdminLayout,
      children: [
        { path: '', redirect: '/dashboard' },
        { path: 'dashboard', component: DashboardView },
        { path: 'servers', component: ServersView },
        { path: 'servers/create', component: ServerFormView },
        { path: 'servers/:id/edit', component: ServerFormView },
        { path: 'servers/:id', component: ServerDetailView },
        { path: 'databases', component: DatabasesView },
        { path: 'databases/:id', component: DatabaseDetailView },
        { path: 'backups', component: BackupsView },
        { path: 'backups/:id', component: BackupDetailView },
        { path: 'backup-configurations', component: SchedulesView },
        { path: 'backup-schedules', component: SchedulesView },
        { path: 'backup-schedules/create', component: ScheduleFormView },
        { path: 'backup-schedules/:id/edit', component: ScheduleFormView },
        { path: 'migrations', component: MigrationsView },
        { path: 'migrations/create', component: MigrationCreateView },
        { path: 'migrations/:id', component: MigrationDetailView },
        { path: 'restores', component: RestoresView },
        { path: 'git-repositories', component: GitView },
        { path: 'jobs', component: JobsView },
        { path: 'jobs/:id', component: JobDetailView },
        { path: 'audit-logs', component: AuditView },
        { path: 'notifications', component: NotificationsView },
        { path: 'settings', component: SettingsView },
        { path: 'users', component: UsersView },
        { path: 'roles', component: RolesView },
        { path: 'health', component: HealthView }
      ]
    }
  ]
})

router.beforeEach((to) => {
  const auth = useAuthStore()
  if (to.path !== '/login' && !auth.isAuthenticated) return '/login'
  if (to.path === '/login' && auth.isAuthenticated) return '/dashboard'
  return true
})

export default router
