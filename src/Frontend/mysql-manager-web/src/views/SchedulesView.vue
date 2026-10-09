<template>
  <section>
    <div class="topbar">
      <div>
        <h1>Schedules</h1>
        <p class="page-lead">One schedule covers every discovered database on a server. Each database keeps its own backups. A failed run does not remove the previous valid backup.</p>
      </div>
      <RouterLink to="/backup-schedules/create"><button type="button">New schedule</button></RouterLink>
    </div>
    <div class="actions filters">
      <label>Server
        <select v-model="serverFilter">
          <option value="">All servers</option>
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <label>Status
        <select v-model="statusFilter">
          <option value="">All</option>
          <option value="enabled">Enabled</option>
          <option value="disabled">Disabled</option>
        </select>
      </label>
      <button type="button" @click="applyFilters">Apply</button>
      <button class="secondary" type="button" @click="clearFilters">Clear</button>
    </div>
    <div class="panel">
      <DataGrid :rows="rows" :filter-fields="['serverName', 'databaseName', 'scheduleText', 'timeZoneId', 'statusText']">
        <Column field="serverName" header="Server" sortable filter :showFilterMenu="false" />
        <Column field="databaseName" header="Coverage" sortable filter :showFilterMenu="false" />
        <Column field="retentionCount" header="Keep" sortable filter :showFilterMenu="false" />
        <Column field="scheduleText" header="Schedule" sortable filter :showFilterMenu="false" />
        <Column field="timeZoneId" header="Time zone" sortable filter :showFilterMenu="false" />
        <Column field="statusText" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.statusText" /></template>
        </Column>
        <Column field="lastEnqueuedText" header="Last queued" sortable filter :showFilterMenu="false" />
        <Column header="">
          <template #body="{ data }">
            <RouterLink :to="`/backup-schedules/${data.id}/edit`"><button class="secondary" type="button">Edit</button></RouterLink>
          </template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

const servers = ref<any[]>([])
const configs = ref<any[]>([])
const serverFilter = ref('')
const statusFilter = ref('')
const applied = ref({ serverId: '', status: '' })

const rows = computed(() => configs.value.map((item) => {
  const server = servers.value.find((entry) => entry.id === item.serverId)
  return {
    ...item,
    serverName: server?.name || item.serverId,
    timeZoneId: item.schedule?.timeZoneId || '',
    scheduleText: describe(item.schedule),
    statusText: item.isEnabled ? 'Enabled' : 'Disabled',
    lastEnqueuedText: formatWhen(item.schedule?.lastEnqueuedAtUtc) || 'Never'
  }
}).filter((row) => {
  if (applied.value.serverId && row.serverId !== applied.value.serverId) return false
  if (applied.value.status === 'enabled' && !row.isEnabled) return false
  if (applied.value.status === 'disabled' && row.isEnabled) return false
  return true
}))

function describe(schedule: any) {
  if (!schedule) return 'Not scheduled'
  if (schedule.kind === 'DailyTime') return `Daily at ${(schedule.dailyTime || '').slice(0, 5)}`
  if (schedule.kind === 'Interval') return `Every ${schedule.intervalHours} hours`
  return schedule.cronExpression || 'Cron'
}

function applyFilters() {
  applied.value = { serverId: serverFilter.value, status: statusFilter.value }
}

function clearFilters() {
  serverFilter.value = ''
  statusFilter.value = ''
  applyFilters()
}

onMounted(async () => {
  servers.value = await getData('/api/servers')
  configs.value = await getData('/api/backup-configurations')
})
</script>
