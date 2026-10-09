import { defineStore } from 'pinia'
import axios from 'axios'

interface AuthPayload {
  accessToken: string
  refreshToken: string
  user: { id: string; email: string; displayName: string; roles: string[]; permissions: string[] }
}

export const useAuthStore = defineStore('auth', {
  state: () => ({
    accessToken: localStorage.getItem('accessToken') ?? '',
    refreshToken: localStorage.getItem('refreshToken') ?? '',
    displayName: localStorage.getItem('displayName') ?? '',
    permissions: JSON.parse(localStorage.getItem('permissions') ?? '[]') as string[]
  }),
  getters: {
    isAuthenticated: (state) => Boolean(state.accessToken)
  },
  actions: {
    can(permission: string) {
      return this.permissions.includes(permission)
    },
    async login(email: string, password: string) {
      const response = await axios.post('/api/auth/login', { email, password })
      this.persist(response.data.data as AuthPayload)
    },
    async refresh() {
      const response = await axios.post('/api/auth/refresh', { refreshToken: this.refreshToken })
      this.persist(response.data.data as AuthPayload)
    },
    async logout() {
      await axios.post('/api/auth/logout', { refreshToken: this.refreshToken }).catch(() => undefined)
      this.accessToken = ''
      this.refreshToken = ''
      this.permissions = []
      localStorage.clear()
    },
    persist(payload: AuthPayload) {
      this.accessToken = payload.accessToken
      this.refreshToken = payload.refreshToken
      this.displayName = payload.user.displayName
      this.permissions = payload.user.permissions
      localStorage.setItem('accessToken', payload.accessToken)
      localStorage.setItem('refreshToken', payload.refreshToken)
      localStorage.setItem('displayName', payload.user.displayName)
      localStorage.setItem('permissions', JSON.stringify(payload.user.permissions))
    }
  }
})
