<template>
  <section>
    <h1>Jobs</h1>
    <p class="page-lead">Backup, copy, and restore jobs. Errors stay on the row until the job finishes or is retried.</p>
    <div class="actions filters">
      <label>From <input v-model="filters.fromDate" type="date" /></label>
      <label>To <input v-model="filters.toDate" type="date" /></label>
      <label>Type
        <select v-model="filters.jobType">
          <option value="">All types</option>
          <option>Backup</option>
          <option>Migration</option>
          <option>Clone</option>
          <option>Restore</option>
        </select>
      </label>
      <label>Status
        <select v-model="filters.status">
          <option value="">All statuses</option>
          <option>Pending</option>
          <option>Running</option>
          <option>Completed</option>
          <option>Failed</option>
          <option>Cancelled</option>
        </select>
      </label>
      <button type="button" @click="load">Apply</button>
      <button class="secondary" type="button" @click="clearDates">Clear dates</button>
    </div>
    <div class="panel">
      <DataGrid :rows="jobs" :filter-fields="['jobType', 'serverName', 'databaseName', 'status', 'errorMessage']">
        <Column field="jobType" header="Type" sortable filter :showFilterMenu="false" />
        <Column field="serverName" header="Server" sortable filter :showFilterMenu="false" />
        <Column field="databaseName" header="Database" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }">
            <RouterLink :to="`/jobs/${data.id}`"><StatusBadge :value="data.status" /></RouterLink>
          </template>
        </Column>
        <Column field="createdAtUtc" header="Created" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="errorMessage" header="Error" sortable filter :showFilterMenu="false" style="min-width: 18rem">
          <template #body="{ data }"><span class="error-text">{{ data.errorMessage }}</span></template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

function dateInput(date: Date) {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}
const today = new Date()
const weekAgo = new Date(today)
weekAgo.setDate(today.getDate() - 7)
const jobs = ref<any[]>([])
const filters = reactive({ jobType: '', status: '', fromDate: dateInput(weekAgo), toDate: dateInput(today) })
async function load() {
  const query = new URLSearchParams()
  if (filters.jobType) query.set('jobType', filters.jobType)
  if (filters.status) query.set('status', filters.status)
  if (filters.fromDate) query.set('fromUtc', new Date(`${filters.fromDate}T00:00:00`).toISOString())
  if (filters.toDate) query.set('toUtc', new Date(`${filters.toDate}T23:59:59`).toISOString())
  jobs.value = await getData('/api/jobs?' + query.toString())
}
async function clearDates() {
  filters.fromDate = ''
  filters.toDate = ''
  await load()
}
onMounted(load)
</script>
