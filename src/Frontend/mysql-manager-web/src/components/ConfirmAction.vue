<template>
  <div v-if="open" class="dialog-backdrop">
    <div class="dialog">
      <h2>{{ title }}</h2>
      <p class="warning">{{ message }}</p>
      <label v-if="confirmation">
        Type {{ confirmation }} to continue
        <input v-model="typed" />
      </label>
      <div class="actions">
        <button class="secondary" type="button" @click="open = false">Cancel</button>
        <button class="danger" type="button" :disabled="confirmation !== '' && typed !== confirmation" @click="accept">Continue</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
const open = defineModel<boolean>('open', { required: true })
const props = defineProps<{ title: string; message: string; confirmation?: string }>()
const emit = defineEmits<{ confirm: [] }>()
const typed = ref('')
function accept() {
  if (props.confirmation && typed.value !== props.confirmation) return
  emit('confirm')
  typed.value = ''
  open.value = false
}
</script>
