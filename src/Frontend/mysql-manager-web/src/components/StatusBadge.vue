<template>
  <span class="badge" :class="tone">{{ value || 'Unknown' }}</span>
</template>

<script setup lang="ts">
import { computed } from 'vue'
const props = defineProps<{ value?: string }>()
const tone = computed(() => {
  const text = (props.value ?? '').toLowerCase()
  if (['completed', 'passed', 'success', 'online', 'sent', 'enabled', 'active'].includes(text)) return 'ok'
  if (['failed', 'offline', 'deleted', 'disabled', 'inactive'].includes(text)) return 'bad'
  if (['running', 'pending', 'preflight'].includes(text)) return 'warn'
  return ''
})
</script>
