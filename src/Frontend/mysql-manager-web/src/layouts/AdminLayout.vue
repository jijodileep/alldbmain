<template>
  <div class="app-shell">
    <header class="app-header">
      <button class="nav-toggle" type="button" aria-label="Open menu" @click="menuOpen = !menuOpen">
        <i class="pi pi-bars" />
      </button>
      <RouterLink class="product" to="/dashboard">
        <span class="product-mark">M</span>
        <span>MySQL Manager</span>
      </RouterLink>
      <span class="header-fill" />
      <button class="user-chip" type="button" @click="toggleUser">
        <span class="avatar">{{ initials }}</span>
        <span class="user-name">{{ auth.displayName || 'Signed in' }}</span>
        <i class="pi pi-chevron-down" />
      </button>
      <Menu ref="userMenu" :model="userItems" :popup="true" />
    </header>
    <div class="app-body">
      <aside class="app-nav" :class="{ open: menuOpen }">
        <nav>
          <section v-for="group in navigation" :key="group.label">
            <p>{{ group.label }}</p>
            <RouterLink
              v-for="item in group.items"
              :key="item.to"
              :to="item.to"
              :class="{ active: isActive(item.to) }"
              @click="menuOpen = false"
            >
              <i :class="item.icon" />
              <span>{{ item.label }}</span>
            </RouterLink>
          </section>
        </nav>
      </aside>
      <button v-if="menuOpen" class="nav-backdrop" type="button" aria-label="Close menu" @click="menuOpen = false" />
      <main class="app-main">
        <div class="app-page">
          <RouterView />
        </div>
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import Menu from 'primevue/menu'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()
const menuOpen = ref(false)
const userMenu = ref()
const initials = computed(() => {
  const name = auth.displayName.trim()
  if (!name) return 'U'
  return name.split(/\s+/).slice(0, 2).map((part) => part[0]?.toUpperCase() ?? '').join('')
})
const userItems = [{ label: 'Sign out', icon: 'pi pi-sign-out', command: signOut }]
const navigation = [
  { label: 'Overview', items: [{ to: '/dashboard', label: 'Dashboard', icon: 'pi pi-home' }] },
  {
    label: 'Infrastructure',
    items: [
      { to: '/servers', label: 'Servers', icon: 'pi pi-server' },
      { to: '/databases', label: 'Databases', icon: 'pi pi-database' },
      { to: '/health', label: 'Health', icon: 'pi pi-heart' }
    ]
  },
  {
    label: 'Backup',
    items: [
      { to: '/backups', label: 'Backups', icon: 'pi pi-save' },
      { to: '/backup-schedules', label: 'Schedules', icon: 'pi pi-calendar' }
    ]
  },
  {
    label: 'Copy',
    items: [
      { to: '/migrations', label: 'Copy jobs', icon: 'pi pi-arrow-right-arrow-left' },
      { to: '/migrations/create', label: 'Copy databases', icon: 'pi pi-clone' },
      { to: '/restores', label: 'Restore', icon: 'pi pi-replay' }
    ]
  },
  { label: 'Git', items: [{ to: '/git-repositories', label: 'Repositories', icon: 'pi pi-github' }] },
  {
    label: 'Operations',
    items: [
      { to: '/jobs', label: 'Jobs', icon: 'pi pi-list' },
      { to: '/audit-logs', label: 'Audit', icon: 'pi pi-book' }
    ]
  },
  {
    label: 'Administration',
    items: [
      { to: '/users', label: 'Users', icon: 'pi pi-users' },
      { to: '/roles', label: 'Roles', icon: 'pi pi-shield' },
      { to: '/notifications', label: 'Notifications', icon: 'pi pi-bell' },
      { to: '/settings', label: 'Settings', icon: 'pi pi-cog' }
    ]
  }
]

function isActive(path: string) {
  if (path === '/migrations') {
    return route.path === '/migrations' || (/^\/migrations\/[^/]+$/.test(route.path) && route.path !== '/migrations/create')
  }
  return route.path === path || route.path.startsWith(`${path}/`)
}

function toggleUser(event: Event) {
  userMenu.value?.toggle(event)
}

async function signOut() {
  await auth.logout()
  await router.replace('/login')
}
</script>
