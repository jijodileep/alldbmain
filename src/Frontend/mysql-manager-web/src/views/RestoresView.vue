<template>
  <section>
    <h1>Restore to a new database</h1>
    <p class="page-lead">Choose the day and the source server, then the backup from that day. Restore creates a new database on the destination server. The source database is not changed. If the new name already exists, the restore uses another name and leaves the existing database as it is.</p>
    <div class="actions filters">
      <label>Date <input v-model="backupDate" type="date" /></label>
      <label>Source server
        <select v-model="sourceServerId">
          <option value="">Select a server</option>
          <option v-for="server in sourceServers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <button type="button" @click="loadBackups">Show backups</button>
    </div>
    <div class="panel">
      <h2>Backups on this day</h2>
      <p v-if="!files.length">No completed backup for this server on this date.</p>
      <DataGrid v-else :rows="files" :filter-fields="['databaseName', 'fileName', 'verificationStatus']">
        <Column field="databaseName" header="Database" sortable filter :showFilterMenu="false" />
        <Column field="fileName" header="Backup" sortable filter :showFilterMenu="false" />
        <Column field="createdAtUtc" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="verificationStatus" header="Verification" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.verificationStatus" /></template>
        </Column>
        <Column header="">
          <template #body="{ data }">
            <button type="button" class="secondary" @click="choose(data)">{{ form.backupFileId === data.id ? 'Selected' : 'Select' }}</button>
          </template>
        </Column>
      </DataGrid>
    </div>
    <form class="form panel" @submit.prevent="submit">
      <p v-if="selected">Selected backup: {{ selected.databaseName }} · {{ selected.fileName }}</p>
      <label>Destination server
        <select v-model="form.destinationServerId" required>
          <option value="">Select a server</option>
          <option v-for="server in destinationServers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <label>New database name <input v-model="form.destinationDatabaseName" required /></label>
      <label>Validation <select v-model="form.validationLevel"><option>Basic</option><option>Standard</option><option>Deep</option></select></label>
      <label>Type RESTORE <input v-model="form.confirmation" /></label>
      <button type="submit" :disabled="form.confirmation !== 'RESTORE' || !form.backupFileId">Queue restore</button>
      <p v-if="message">{{ message }}</p>
      <p v-if="errorText" class="error">{{ errorText }}</p>
    </form>
    <div class="panel">
      <DataGrid :rows="jobs" :filter-fields="['destinationDatabaseName', 'status', 'sourceModification', 'errorMessage']">
        <Column field="destinationDatabaseName" header="New database" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.status" /></template>
        </Column>
        <Column field="sourceModification" header="Source modification" sortable filter :showFilterMenu="false" />
        <Column field="createdAtUtc" header="Created" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="errorMessage" header="Error" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><span class="error-text">{{ data.errorMessage }}</span></template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import Column from 'primevue/column'
import { apiErrorMessage, getData, postData } from '../api/http'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { formatWhen } from '../utils/format'

function dateInput(date: Date) {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

const servers = ref<any[]>([])
const files = ref<any[]>([])
const jobs = ref<any[]>([])
const message = ref('')
const errorText = ref('')
const backupDate = ref(dateInput(new Date()))
const sourceServerId = ref('')
const form = reactive({ backupFileId: '', destinationServerId: '', destinationDatabaseName: '', validationLevel: 'Standard', confirmation: '' })

const sourceServers = computed(() => servers.value.filter((server) => server.isActive !== false))
const destinationServers = computed(() => sourceServers.value.filter((server) => (server.credentials ?? []).some((item: { purpose?: string }) => item.purpose === 'DestinationWrite')))
const selected = computed(() => files.value.find((file) => file.id === form.backupFileId))

function choose(file: { id: string; databaseName?: string }) {
  form.backupFileId = file.id
  form.destinationDatabaseName = file.databaseName || form.destinationDatabaseName
}

async function loadBackups() {
  errorText.value = ''
  form.backupFileId = ''
  if (!backupDate.value || !sourceServerId.value) {
    files.value = []
    return
  }
  const query = new URLSearchParams()
  query.set('serverId', sourceServerId.value)
  query.set('status', 'Completed')
  query.set('fromUtc', new Date(`${backupDate.value}T00:00:00`).toISOString())
  query.set('toUtc', new Date(`${backupDate.value}T23:59:59`).toISOString())
  const rows = await getData<any[]>('/api/backups?' + query.toString())
  files.value = rows.filter((file) => file.verificationStatus === 'Passed')
}

async function submit() {
  message.value = ''
  errorText.value = ''
  try {
    const result = await postData<{ detail?: string }>('/api/restores', form)
    message.value = result.data?.detail || result.message
    form.confirmation = ''
    jobs.value = await getData('/api/restores')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}

watch([backupDate, sourceServerId], () => { void loadBackups() })

onMounted(async () => {
  servers.value = await getData('/api/servers')
  jobs.value = await getData('/api/restores')
  const first = sourceServers.value[0]
  if (first) sourceServerId.value = first.id
})
</script>
