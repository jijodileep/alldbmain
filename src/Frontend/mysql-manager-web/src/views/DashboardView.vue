<template>
  <section>
    <h1>Dashboard</h1>
    <div class="grid cards">
      <article v-for="card in cards" :key="card.label" class="card"><span>{{ card.label }}</span><strong>{{ card.value }}</strong></article>
    </div>
    <div class="grid" style="grid-template-columns: 1fr 1fr; margin-top: 12px">
      <article class="panel"><h2>Backup outcomes</h2><Chart type="bar" :data="outcomeChart" /></article>
      <article class="panel"><h2>Storage growth</h2><Chart type="line" :data="growthChart" /></article>
    </div>
    <article class="panel" style="margin-top: 12px">
      <h2>Recent activity</h2>
      <DataGrid :rows="activity" :filter-fields="['kind', 'message']">
        <Column field="timestamp" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.timestamp) }}</template>
        </Column>
        <Column field="kind" header="Kind" sortable filter :showFilterMenu="false" />
        <Column field="message" header="Message" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </article>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Chart from 'primevue/chart'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

interface Point { label: string; value: number }
interface Dashboard {
  totalServers: number; onlineServers: number; offlineServers: number; databases: number
  successfulBackupsToday: number; failedBackupsToday: number; backupsRunning: number; migrationsRunning: number
  storageUsedBytes: number; storageFreeBytes: number
  recentActivity: { timestamp: string; kind: string; message: string }[]
  backupOutcomes: Point[]; storageGrowth: Point[]
}
const data = ref<Dashboard | null>(null)
const activity = computed(() => (data.value?.recentActivity ?? []).map((item, index) => ({ ...item, id: `${item.timestamp}-${index}` })))
const cards = computed(() => data.value ? [
  { label: 'Total servers', value: data.value.totalServers },
  { label: 'Online servers', value: data.value.onlineServers },
  { label: 'Offline servers', value: data.value.offlineServers },
  { label: 'Databases', value: data.value.databases },
  { label: 'Successful backups today', value: data.value.successfulBackupsToday },
  { label: 'Failed backups today', value: data.value.failedBackupsToday },
  { label: 'Backups running', value: data.value.backupsRunning },
  { label: 'Migrations running', value: data.value.migrationsRunning },
  { label: 'Storage used', value: data.value.storageUsedBytes },
  { label: 'Storage available', value: data.value.storageFreeBytes }
] : [])
const outcomeChart = computed(() => chart(data.value?.backupOutcomes ?? []))
const growthChart = computed(() => chart(data.value?.storageGrowth ?? []))
function chart(points: Point[]) {
  return { labels: points.map(p => p.label), datasets: [{ data: points.map(p => p.value) }] }
}
onMounted(async () => { data.value = await getData<Dashboard>('/api/dashboard') })
</script>
