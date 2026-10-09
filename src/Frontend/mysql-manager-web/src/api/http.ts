import axios from 'axios'
import { useAuthStore } from '../stores/auth'

export const http = axios.create({ baseURL: '/' })

http.interceptors.request.use((config) => {
  const auth = useAuthStore()
  if (auth.accessToken) {
    config.headers.Authorization = `Bearer ${auth.accessToken}`
  }
  return config
})

export function apiErrorMessage(error: unknown): string {
  const data = (error as { response?: { data?: { message?: string; errors?: { message?: string }[] } } })?.response?.data
  const fieldErrors = (data?.errors ?? []).map((item) => item.message).filter((item): item is string => !!item)
  if (fieldErrors.length) return fieldErrors.join(' ')
  if (data?.message) return data.message
  return 'The request failed. Check that the API is running.'
}

function notify(severity: 'error' | 'warn' | 'success', summary: string, message: string) {
  window.dispatchEvent(new CustomEvent('api-notice', { detail: { severity, summary, message } }))
}

function endSession() {
  const auth = useAuthStore()
  auth.accessToken = ''
  auth.refreshToken = ''
  auth.displayName = ''
  auth.permissions = []
  localStorage.removeItem('accessToken')
  localStorage.removeItem('refreshToken')
  localStorage.removeItem('displayName')
  localStorage.removeItem('permissions')
  if (window.location.pathname !== '/login') {
    void import('../router').then(({ default: appRouter }) => appRouter.replace('/login'))
  }
}

http.interceptors.response.use(undefined, async (error) => {
  const auth = useAuthStore()
  const original = error.config
  const url = String(original?.url ?? '')
  if (error.response?.status === 401) {
    if (url.includes('/api/auth/login')) {
      return Promise.reject(error)
    }
    if (auth.refreshToken && !original?.retried) {
      original.retried = true
      try {
        await auth.refresh()
        original.headers.Authorization = `Bearer ${auth.accessToken}`
        return http(original)
      } catch {
        endSession()
        notify('warn', 'Sign-in required', 'Your session ended. Sign in to continue.')
        return Promise.reject(error)
      }
    }
    endSession()
    notify('warn', 'Sign-in required', 'Sign in to continue.')
    return Promise.reject(error)
  }
  notify('error', 'Request failed', apiErrorMessage(error))
  return Promise.reject(error)
})

export interface ApiResponse<T> {
  success: boolean
  messageKey: string
  message: string
  data: T
  errors: { field: string; message: string }[]
}

export async function getData<T>(url: string): Promise<T> {
  const response = await http.get<ApiResponse<T>>(url)
  return response.data.data
}

export async function postData<T>(url: string, body?: unknown): Promise<ApiResponse<T>> {
  const response = await http.post<ApiResponse<T>>(url, body ?? {})
  return response.data
}
