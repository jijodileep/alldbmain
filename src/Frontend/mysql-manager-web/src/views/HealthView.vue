<template>
  <section>
    <h1>Health</h1>
    <p class="page-lead">Remote CPU, memory, and host disk stay unavailable until a backup agent reports them. Connectivity, backup age, and backup-volume free space are monitored.</p>
    <div class="panel">
      <DataGrid :rows="rows" :filter-fields="['name', 'status', 'version', 'lastStatus']">
        <Column field="name" header="Server" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.status" /></template>
        </Column>
        <Column field="version" header="MySQL version" sortable filter :showFilterMenu="false" />
        <Column field="latency" header="Latency ms" sortable filter :showFilterMenu="false" />
        <Column field="databaseCount" header="Databases" sortable filter :showFilterMenu="false" />
        <Column field="lastBackup" header="Last backup" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.lastBackup) }}</template>
        </Column>
        <Column field="lastStatus" header="Last backup status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.lastStatus" /></template>
        </Column>
        <Column header="">
          <template #body="{ data }"><button type="button" @click="load(data.id)">Refresh</button></template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

const servers = ref<any[]>([])
const health = reactive<Record<string, any>>({})
const rows = computed(() => servers.value.map((server) => ({
  id: server.id,
  name: server.name,
  status: health[server.id]?.isOnline ? 'Online' : 'Offline',
  version: health[server.id]?.mySqlVersion || '',
  latency: health[server.id]?.latencyMs,
  databaseCount: health[server.id]?.databaseCount,
  lastBackup: health[server.id]?.lastBackupAtUtc,
  lastStatus: health[server.id]?.lastBackupStatus || ''
})))
async function load(id: string) { health[id] = await getData(`/api/servers/${id}/health`) }
onMounted(async () => { servers.value = await getData('/api/servers'); for (const server of servers.value) await load(server.id) })
</script>
