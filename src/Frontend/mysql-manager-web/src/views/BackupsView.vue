<template>
  <section>
    <h1>Backups</h1>
    <p class="page-lead">Showing backups in the selected dates. Clear the dates to see every artifact.</p>
    <div class="actions filters">
      <label>From <input v-model="fromDate" type="date" /></label>
      <label>To <input v-model="toDate" type="date" /></label>
      <label>Server
        <select v-model="serverId">
          <option value="">All servers</option>
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <label>Status
        <select v-model="status">
          <option value="">All statuses</option>
          <option>Completed</option>
          <option>Failed</option>
          <option>Pending</option>
          <option>Running</option>
        </select>
      </label>
      <button type="button" @click="load">Apply</button>
      <button class="secondary" type="button" @click="clearDates">Clear dates</button>
    </div>
    <div class="panel">
      <DataGrid :rows="files" :filter-fields="['fileName', 'serverName', 'databaseName', 'status', 'verificationStatus', 'createdAtUtc']">
        <Column field="createdAtUtc" header="Created" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="serverName" header="Server" sortable filter :showFilterMenu="false" />
        <Column field="databaseName" header="Database" sortable filter :showFilterMenu="false" />
        <Column field="fileName" header="File" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><RouterLink :to="`/backups/${data.id}`">{{ data.fileName }}</RouterLink></template>
        </Column>
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.status" /></template>
        </Column>
        <Column field="verificationStatus" header="Verification" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.verificationStatus" /></template>
        </Column>
        <Column field="compressedSizeBytes" header="Compressed" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatBytes(data.compressedSizeBytes) }}</template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData } from '../api/http'
import { formatBytes, formatWhen } from '../utils/format'

function dateInput(date: Date) {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}
const today = new Date()
const weekAgo = new Date(today)
weekAgo.setDate(today.getDate() - 7)
const fromDate = ref(dateInput(weekAgo))
const toDate = ref(dateInput(today))
const serverId = ref('')
const status = ref('Completed')
const servers = ref<any[]>([])
const files = ref<any[]>([])

async function load() {
  const query = new URLSearchParams()
  if (serverId.value) query.set('serverId', serverId.value)
  if (status.value) query.set('status', status.value)
  if (fromDate.value) query.set('fromUtc', new Date(`${fromDate.value}T00:00:00`).toISOString())
  if (toDate.value) query.set('toUtc', new Date(`${toDate.value}T23:59:59`).toISOString())
  files.value = await getData('/api/backups?' + query.toString())
}
async function clearDates() {
  fromDate.value = ''
  toDate.value = ''
  await load()
}
onMounted(async () => {
  servers.value = await getData('/api/servers')
  await load()
})
</script>
