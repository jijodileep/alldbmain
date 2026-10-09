<template>
  <section>
    <h1>Databases</h1>
    <p class="page-lead">Discovery is read-only. Deactivating a row does not drop the MySQL database.</p>
    <div class="actions filters">
      <label>Server
        <select v-model="serverId">
          <option value="">All servers</option>
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <label>Name <input v-model="nameQuery" type="search" placeholder="Database name" /></label>
      <label>Backup
        <select v-model="backup">
          <option value="">All</option>
          <option value="yes">Included</option>
          <option value="no">Not included</option>
        </select>
      </label>
      <label>Status
        <select v-model="status">
          <option value="">All</option>
          <option value="active">Active</option>
          <option value="inactive">Inactive</option>
        </select>
      </label>
      <button type="button" @click="apply">Apply</button>
      <button class="secondary" type="button" @click="clearFilters">Clear</button>
    </div>
    <p v-if="loaded && !all.length">No databases are in the catalog yet. Open the server and choose Discover databases after the backup account has been granted access to the real schema names.</p>
    <div class="panel">
      <DataGrid :rows="databases" :filter-fields="['serverName', 'name', 'charset', 'collation', 'backupText', 'statusText']">
        <Column field="serverName" header="Server" sortable filter :showFilterMenu="false" />
        <Column field="name" header="Database" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><RouterLink :to="`/databases/${data.id}`">{{ data.name }}</RouterLink></template>
        </Column>
        <Column field="sizeBytes" header="Size" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatBytes(data.sizeBytes) }}</template>
        </Column>
        <Column field="tableCount" header="Tables" sortable filter :showFilterMenu="false" />
        <Column field="estimatedRows" header="Rows" sortable filter :showFilterMenu="false" />
        <Column field="charset" header="Charset" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ data.charset }} / {{ data.collation }}</template>
        </Column>
        <Column field="backupText" header="Backup" sortable filter :showFilterMenu="false">
          <template #body="{ data }">
            <input type="checkbox" :checked="data.isSelectedForBackup" @change="toggle(data)" />
          </template>
        </Column>
        <Column field="statusText" header="Status" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Column from 'primevue/column'
import { getData, postData } from '../api/http'
import DataGrid from '../components/DataGrid.vue'
import { formatBytes } from '../utils/format'

const all = ref<any[]>([])
const servers = ref<any[]>([])
const loaded = ref(false)
const serverId = ref('')
const nameQuery = ref('')
const backup = ref('')
const status = ref('')
const applied = ref({ serverId: '', name: '', backup: '', status: '' })
const databases = computed(() => all.value.filter((row) => {
  if (applied.value.serverId && row.serverId !== applied.value.serverId) return false
  if (applied.value.name && !String(row.name).toLowerCase().includes(applied.value.name.toLowerCase())) return false
  if (applied.value.backup === 'yes' && !row.isSelectedForBackup) return false
  if (applied.value.backup === 'no' && row.isSelectedForBackup) return false
  if (applied.value.status === 'active' && !row.isActive) return false
  if (applied.value.status === 'inactive' && row.isActive) return false
  return true
}).map((row) => ({
  ...row,
  backupText: row.isSelectedForBackup ? 'Included' : 'Not included',
  statusText: row.isActive ? 'Active' : 'Inactive'
})))
function apply() {
  applied.value = { serverId: serverId.value, name: nameQuery.value.trim(), backup: backup.value, status: status.value }
}
function clearFilters() {
  serverId.value = ''
  nameQuery.value = ''
  backup.value = ''
  status.value = ''
  apply()
}
async function load() {
  all.value = await getData('/api/databases')
  loaded.value = true
}
async function toggle(db: any) {
  const source = all.value.find((item) => item.id === db.id)
  if (!source) return
  source.isSelectedForBackup = !source.isSelectedForBackup
  const selected = all.value.filter((item) => item.serverId === source.serverId && item.isSelectedForBackup).map((item) => item.id)
  await postData('/api/databases/selection', { serverId: source.serverId, databaseIds: selected })
}
onMounted(async () => {
  servers.value = await getData('/api/servers')
  await load()
})
</script>
