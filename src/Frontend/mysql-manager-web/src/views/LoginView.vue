<template>
  <div class="auth-screen">
    <form class="auth-card" @submit.prevent="submit">
      <div class="auth-brand">
        <span class="product-mark">M</span>
        <div>
          <h1>MySQL Manager</h1>
          <p>Sign in to the console</p>
        </div>
      </div>
      <label>
        Email
        <InputText v-model="email" type="email" autocomplete="username" required fluid />
      </label>
      <label>
        Password
        <Password v-model="password" :feedback="false" toggleMask autocomplete="current-password" required fluid />
      </label>
      <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
      <Button type="submit" label="Sign in" icon="pi pi-sign-in" :loading="busy" fluid />
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Password from 'primevue/password'
import { apiErrorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'

const email = ref('')
const password = ref('')
const error = ref('')
const busy = ref(false)
const auth = useAuthStore()
const router = useRouter()

async function submit() {
  error.value = ''
  busy.value = true
  try {
    await auth.login(email.value, password.value)
    await router.replace('/dashboard')
  } catch (reason) {
    error.value = apiErrorMessage(reason)
  } finally {
    busy.value = false
  }
}
</script>
