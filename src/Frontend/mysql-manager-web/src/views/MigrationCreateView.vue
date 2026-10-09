<template>
  <section>
    <h1>Copy databases</h1>
    <p class="page-lead">Select every database you want to copy. The source stays unchanged, and an existing destination database is never dropped.</p>
    <form class="form panel wide" @submit.prevent="submit">
      <label>Source server
        <select v-model="form.sourceServerId" @change="loadSource">
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <h2>Source databases</h2>
      <DataTable
        v-model:selection="selected"
        v-model:filters="tableFilters"
        :value="visibleDatabases"
        dataKey="id"
        paginator
        :rows="10"
        :rowsPerPageOptions="[10, 25, 50, 100]"
        stripedRows
        size="small"
        filterDisplay="row"
        :globalFilterFields="['name']"
      >
        <template #header>
          <div class="table-toolbar">
            <span>{{ selected.length }} of {{ visibleDatabases.length }} selected</span>
            <div class="actions">
              <button class="secondary" type="button" @click="selected = [...visibleDatabases]">Select all</button>
              <button class="secondary" type="button" @click="selected = []">Clear</button>
            </div>
          </div>
        </template>
        <template #empty>Discover databases on the source server first.</template>
        <Column selectionMode="multiple" headerStyle="width: 3rem" />
        <Column field="name" header="Database" sortable filter :showFilterMenu="false" />
        <Column field="tableCount" header="Tables" sortable filter :showFilterMenu="false" />
        <Column field="sizeBytes" header="Size" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatBytes(data.sizeBytes) }}</template>
        </Column>
      </DataTable>
      <h2>Destination</h2>
      <label>Destination server
        <select v-model="form.destinationServerId">
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <p v-if="sameServer" class="warning">Same-server clone. Each new database uses the suffix below. The original database is not renamed or removed.</p>
      <p v-else class="warning">Each selected database keeps its name on the destination server. Add a suffix only when you want a different name.</p>
      <div v-if="form.destinationServerId && !destinationReady" class="warning">
        <p v-if="savedAccounts.length">
          {{ destinationName }} already has a saved login:
          <strong v-for="item in savedAccounts" :key="item.id">{{ item.username }} ({{ accountLabel(item.purpose) }})</strong>.
          That password is already stored. This form is not asking for it again.
        </p>
        <p>That saved login can only read databases. A clone creates a new database, so it needs a different account that is allowed to create databases.</p>
        <div class="grid two">
          <label>Destination username <input v-model="credential.username" autocomplete="off" /></label>
          <label>Password <input v-model="credential.password" type="password" autocomplete="new-password" /></label>
        </div>
        <button type="button" :disabled="savingCredential || !credential.username || !credential.password" @click="saveCredential">Save destination account</button>
      </div>
      <div class="grid two">
        <label>Name suffix
          <input v-model="form.nameSuffix" :placeholder="sameServer ? '_copy' : 'Leave empty to keep the same names'" />
        </label>
        <label>Validation
          <select v-model="form.validationLevel"><option>Basic</option><option>Standard</option><option>Deep</option></select>
        </label>
      </div>
      <div class="actions">
        <button type="submit" :disabled="busy || !selected.length || !destinationReady">Queue {{ selected.length }} {{ sameServer ? 'clones' : 'copies' }}</button>
        <RouterLink to="/migrations">View copy jobs</RouterLink>
      </div>
      <p v-if="message">{{ message }}</p>
      <p v-if="errorText" class="error">{{ errorText }}</p>
    </form>
    <div v-if="skipped.length" class="panel" style="margin-top: 12px">
      <h2>Not queued</h2>
      <DataGrid :rows="skipped" :filter-fields="['databaseName', 'reason']">
        <Column field="databaseName" header="Database" sortable filter :showFilterMenu="false" />
        <Column field="reason" header="Reason" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import Column from 'primevue/column'
import DataTable from 'primevue/datatable'
import { apiErrorMessage, getData, http, postData } from '../api/http'
import DataGrid from '../components/DataGrid.vue'
import { formatBytes } from '../utils/format'

const servers = ref<any[]>([])
const sourceDatabases = ref<any[]>([])
const selected = ref<any[]>([])
const tableFilters = ref({
  global: { value: null as string | null, matchMode: 'contains' },
  name: { value: null as string | null, matchMode: 'contains' },
  tableCount: { value: null as string | null, matchMode: 'contains' },
  sizeBytes: { value: null as string | null, matchMode: 'contains' }
})
const skipped = ref<any[]>([])
const message = ref('')
const errorText = ref('')
const busy = ref(false)
const savingCredential = ref(false)
const credential = reactive({ username: '', password: '' })
const form = reactive({
  sourceServerId: '',
  destinationServerId: '',
  nameSuffix: '',
  validationLevel: 'Standard'
})
const visibleDatabases = computed(() => sourceDatabases.value.filter((item) => item.serverId === form.sourceServerId && item.isActive !== false))
const sameServer = computed(() => !!form.sourceServerId && form.sourceServerId === form.destinationServerId)
const destinationServer = computed(() => servers.value.find((item) => item.id === form.destinationServerId))
const destinationName = computed(() => destinationServer.value?.name || 'The destination server')
const destinationReady = computed(() => !!destinationServer.value?.credentials?.some((item: { purpose: string }) => item.purpose === 'DestinationWrite'))
const savedAccounts = computed(() => destinationServer.value?.credentials ?? [])
function accountLabel(purpose: string) {
  return purpose === 'BackupReadOnly' ? 'backup, read-only' : purpose === 'DestinationWrite' ? 'destination' : purpose
}

watch(sameServer, (value) => {
  if (value && !form.nameSuffix) form.nameSuffix = '_copy'
})

async function saveCredential() {
  errorText.value = ''
  message.value = ''
  savingCredential.value = true
  try {
    await http.put(`/api/servers/${form.destinationServerId}/credentials`, {
      purpose: 'DestinationWrite',
      username: credential.username,
      password: credential.password,
      secretProvider: 'LocalEncrypted'
    })
    credential.password = ''
    servers.value = await getData('/api/servers')
    message.value = 'Destination account saved. Select the databases and queue the clone.'
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  } finally {
    savingCredential.value = false
  }
}

async function loadSource() {
  selected.value = []
  sourceDatabases.value = form.sourceServerId ? await getData(`/api/databases?serverId=${form.sourceServerId}`) : []
}

async function submit() {
  message.value = ''
  errorText.value = ''
  skipped.value = []
  if (sameServer.value && !form.nameSuffix.trim()) {
    errorText.value = 'Enter a name suffix so the clone does not use the same database name.'
    return
  }
  busy.value = true
  try {
    const result = await postData<{ queuedCount: number; firstJobId?: string; skipped: { databaseName: string; reason: string }[] }>('/api/migrations/batch', {
      sourceServerId: form.sourceServerId,
      sourceDatabaseIds: selected.value.map((item) => item.id),
      destinationServerId: form.destinationServerId,
      nameSuffix: form.nameSuffix,
      mode: sameServer.value ? 'Clone' : 'Copy',
      validationLevel: form.validationLevel,
      acknowledgeVersionMismatch: true
    })
    message.value = result.message
    skipped.value = (result.data?.skipped ?? []).map((item, index) => ({ ...item, id: `${item.databaseName}-${index}` }))
    if ((result.data?.queuedCount ?? 0) === 0) {
      window.dispatchEvent(new CustomEvent('api-notice', { detail: { severity: 'warn', summary: 'Nothing queued', message: result.message } }))
    }
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  } finally {
    busy.value = false
  }
}

onMounted(async () => {
  servers.value = await getData('/api/servers')
  if (servers.value.length) {
    form.sourceServerId = servers.value[0].id
    form.destinationServerId = servers.value[0].id
    await loadSource()
  }
})
</script>
