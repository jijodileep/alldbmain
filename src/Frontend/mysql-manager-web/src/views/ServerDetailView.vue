<template>
  <section v-if="server">
    <h1>{{ server.name }}</h1>
    <p>{{ server.description }}</p>
    <p>{{ server.host }}:{{ server.port }} · {{ server.environment }} · timeout {{ server.connectionTimeoutSeconds }}s</p>
    <div class="actions">
      <button type="button" @click="test">Test connection</button>
      <button type="button" class="secondary" @click="discover">Discover databases</button>
      <RouterLink :to="`/servers/${server.id}/edit`"><button type="button" class="secondary">Edit</button></RouterLink>
      <button type="button" class="danger" @click="confirmDelete = true">Delete</button>
    </div>
    <p v-if="message">{{ message }}</p>
    <p v-if="errorText" class="error">{{ errorText }}</p>
    <article v-if="testResult" class="panel">
      <p>Connected: {{ testResult.connected }}</p>
      <p>MySQL version: {{ testResult.mySqlVersion || 'Unavailable' }}</p>
      <p>Uptime seconds: {{ testResult.uptimeSeconds ?? 'Unavailable' }}</p>
      <p>Database count: {{ testResult.databaseCount }}</p>
      <p>Backup volume free bytes: {{ testResult.availableDiskBytes ?? 'Unavailable' }}</p>
      <p>Latency ms: {{ testResult.connectionLatencyMs }}</p>
      <p>{{ testResult.message }}</p>
    </article>
    <h2>Credentials</h2>
    <div class="panel">
      <DataGrid :rows="server.credentials ?? []" :filter-fields="['purpose', 'username']">
        <Column field="purpose" header="Purpose" sortable filter :showFilterMenu="false" />
        <Column field="username" header="Username" sortable filter :showFilterMenu="false" />
        <Column field="isPrivilegeValidated" header="Privilege check" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
    <form class="form" @submit.prevent="saveDestination">
      <h2>Destination credential</h2>
      <p>Used only when creating a new destination database. It is never used against the source.</p>
      <label>Username <input v-model="destUser" required /></label>
      <label>Password <input v-model="destPassword" type="password" required /></label>
      <button type="submit">Save destination credential</button>
    </form>
    <ConfirmAction v-model:open="confirmDelete" title="Remove server" message="This removes the server from this application only. Databases on that MySQL host are not dropped, and backup files already stored are kept." confirmation="DELETE" @confirm="remove" />
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConfirmAction from '../components/ConfirmAction.vue'
import Column from 'primevue/column'
import { apiErrorMessage, getData, http } from '../api/http'
import DataGrid from '../components/DataGrid.vue'
const route = useRoute()
const router = useRouter()
const server = ref<any>(null)
const testResult = ref<any>(null)
const confirmDelete = ref(false)
const destUser = ref('')
const destPassword = ref('')
const message = ref('')
const errorText = ref('')
async function load() { server.value = await getData(`/api/servers/${route.params.id}`) }
async function test() {
  message.value = ''
  errorText.value = ''
  try {
    testResult.value = (await http.post(`/api/servers/${route.params.id}/test-connection?purpose=BackupReadOnly`)).data.data
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function discover() {
  message.value = ''
  errorText.value = ''
  try {
    const rows = await getData<any[]>(`/api/servers/${route.params.id}/databases`)
    if (!rows.length) {
      errorText.value = 'No databases are visible to this backup account.'
      return
    }
    await router.push('/databases')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function saveDestination() {
  errorText.value = ''
  message.value = ''
  try {
    await http.put(`/api/servers/${route.params.id}/credentials`, { purpose: 'DestinationWrite', username: destUser.value, password: destPassword.value, secretProvider: 'LocalEncrypted' })
    destPassword.value = ''
    message.value = 'Destination credential saved. It is used only to create new databases.'
    await load()
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function remove() {
  errorText.value = ''
  try {
    await http.delete(`/api/servers/${route.params.id}`, { data: { confirmation: 'DELETE' } })
    await router.push('/servers')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
onMounted(load)
</script>
