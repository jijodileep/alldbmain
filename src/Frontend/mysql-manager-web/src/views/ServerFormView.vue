<template>
  <form class="form panel" @submit.prevent="submit">
    <h1>{{ editing ? 'Edit server' : 'Register server' }}</h1>
    <p class="page-lead">{{ editing ? 'Saving changes updates this registration only. Databases on the MySQL host are left as they are.' : 'The backup account must be read-only. The password is encrypted and is not returned by the API.' }}</p>
    <label>Name <input v-model="form.name" required /></label>
    <label>Description <input v-model="form.description" /></label>
    <label>Host <input v-model="form.host" required /></label>
    <label>Port <input v-model.number="form.port" type="number" required /></label>
    <label>Environment
      <select v-model="form.environment"><option>Production</option><option>Staging</option><option>Development</option><option>Testing</option><option>Other</option></select>
    </label>
    <label><input v-model="form.isActive" type="checkbox" /> Active</label>
    <h2>Backup account</h2>
    <p class="page-lead">Read-only. Used to list and back up databases. It cannot create a clone. {{ editing ? 'Leave the password blank to keep the saved account.' : '' }}</p>
    <label>Username <input v-model="form.username" :required="!editing" autocomplete="off" /></label>
    <label>Password <input v-model="form.password" type="password" :required="!editing" autocomplete="new-password" /></label>
    <h2>Destination account</h2>
    <p class="page-lead">Optional. Required before a copy or clone. This account must be allowed to create databases. {{ editing ? 'Leave the password blank to keep the saved account.' : '' }}</p>
    <label>Username <input v-model="form.destinationUsername" autocomplete="off" /></label>
    <label>Password <input v-model="form.destinationPassword" type="password" autocomplete="new-password" /></label>
    <label>Timeout seconds <input v-model.number="form.connectionTimeoutSeconds" type="number" /></label>
    <label><input v-model="form.sslEnabled" type="checkbox" /> SSL enabled</label>
    <label>SSL certificate <textarea v-model="form.sslCertificatePem" rows="4" :placeholder="editing && hasCertificate ? 'A certificate is already saved. Paste a new one only to replace it.' : ''" /></label>
    <div class="actions">
      <button type="submit">{{ editing ? 'Save changes' : 'Save' }}</button>
      <RouterLink :to="editing ? `/servers/${route.params.id}` : '/servers'"><button type="button" class="secondary">Cancel</button></RouterLink>
    </div>
    <p v-if="errorText" class="error">{{ errorText }}</p>
    <p v-if="message">{{ message }}</p>
  </form>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { apiErrorMessage, getData, http, postData } from '../api/http'

const route = useRoute()
const router = useRouter()
const editing = computed(() => route.path.endsWith('/edit'))
const message = ref('')
const errorText = ref('')
const hasCertificate = ref(false)
const savedBackupUser = ref('')
const savedDestinationUser = ref('')
const form = reactive({ name: '', description: '', host: '', port: 3306, environment: 'Production', username: '', password: '', destinationUsername: '', destinationPassword: '', connectionTimeoutSeconds: 15, sslEnabled: false, sslCertificatePem: '', isActive: true, timeZoneId: 'UTC' })

onMounted(async () => {
  if (!editing.value) return
  const server = await getData<any>(`/api/servers/${route.params.id}`)
  form.name = server.name
  form.description = server.description ?? ''
  form.host = server.host
  form.port = server.port
  form.environment = server.environment
  form.connectionTimeoutSeconds = server.connectionTimeoutSeconds
  form.sslEnabled = server.sslEnabled
  form.isActive = server.isActive
  form.timeZoneId = server.timeZoneId || 'UTC'
  hasCertificate.value = !!server.hasSslCertificate
  const backup = (server.credentials ?? []).find((item: { purpose: string }) => item.purpose === 'BackupReadOnly')
  const destination = (server.credentials ?? []).find((item: { purpose: string }) => item.purpose === 'DestinationWrite')
  savedBackupUser.value = backup?.username ?? ''
  savedDestinationUser.value = destination?.username ?? ''
  form.username = savedBackupUser.value
  form.destinationUsername = savedDestinationUser.value
})

async function submit() {
  message.value = ''
  errorText.value = ''
  try {
    if (editing.value) {
      if ((form.username !== savedBackupUser.value && !form.password) || (form.destinationUsername !== savedDestinationUser.value && form.destinationUsername && !form.destinationPassword)) {
        errorText.value = 'Enter the password when you change a username. A blank password keeps the saved account.'
        return
      }
      await http.put(`/api/servers/${route.params.id}`, {
        name: form.name,
        description: form.description,
        host: form.host,
        port: form.port,
        environment: form.environment,
        sslEnabled: form.sslEnabled,
        sslCertificatePem: form.sslCertificatePem.trim() ? form.sslCertificatePem : null,
        connectionTimeoutSeconds: form.connectionTimeoutSeconds,
        isActive: form.isActive,
        timeZoneId: form.timeZoneId
      })
      if (form.password) {
        await http.put(`/api/servers/${route.params.id}/credentials`, { purpose: 'BackupReadOnly', username: form.username, password: form.password, secretProvider: 'LocalEncrypted' })
      }
      if (form.destinationPassword) {
        await http.put(`/api/servers/${route.params.id}/credentials`, { purpose: 'DestinationWrite', username: form.destinationUsername, password: form.destinationPassword, secretProvider: 'LocalEncrypted' })
      }
      await router.push(`/servers/${route.params.id}`)
      return
    }

    const credentials: { purpose: string; username: string; password: string; secretProvider: string; externalSecretName: null }[] = [{ purpose: 'BackupReadOnly', username: form.username, password: form.password, secretProvider: 'LocalEncrypted', externalSecretName: null }]
    if (form.destinationUsername && form.destinationPassword) {
      credentials.push({ purpose: 'DestinationWrite', username: form.destinationUsername, password: form.destinationPassword, secretProvider: 'LocalEncrypted', externalSecretName: null })
    }
    const result = await postData<{ id: string }>('/api/servers', { ...form, credentials })
    message.value = result.message
    if (result.data?.id) await router.push(`/servers/${result.data.id}`)
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
</script>
