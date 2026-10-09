<template>
  <Toast position="top-right" />
  <RouterView />
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import Toast from 'primevue/toast'
import { useToast } from 'primevue/usetoast'

const toast = useToast()

function onNotice(event: Event) {
  const detail = (event as CustomEvent<{ severity?: 'error' | 'warn' | 'success'; summary?: string; message?: string }>).detail
  toast.add({
    severity: detail?.severity ?? 'error',
    summary: detail?.summary ?? 'Request failed',
    detail: detail?.message ?? 'The request failed.',
    life: 8000
  })
}

onMounted(() => window.addEventListener('api-notice', onNotice))
onUnmounted(() => window.removeEventListener('api-notice', onNotice))
</script>
