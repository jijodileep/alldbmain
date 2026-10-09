<template>
  <section v-if="job">
    <h1>{{ job.mode }} {{ job.sourceDatabaseName }}</h1>
    <p class="warning">Source modification: {{ job.sourceModification }}</p>
    <p>{{ job.sourceServerName }} / {{ job.sourceDatabaseName }} → {{ job.destinationServerName }} / {{ job.destinationDatabaseName }}</p>
    <p><StatusBadge :value="job.status" /> {{ job.currentStep }}</p>
    <div class="progress"><span :style="{ width: (job.progressPercentage ?? 0) + '%' }"></span></div>
    <p v-if="job.progressPercentage == null">Running</p>
    <p v-else>Overall {{ job.progressPercentage }}%</p>
    <p v-if="job.errorMessage" class="error">{{ job.errorMessage }}</p>
    <div class="panel">
      <DataGrid :rows="logs" :filter-fields="['level', 'message']">
        <Column field="createdAtUtc" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="level" header="Level" sortable filter :showFilterMenu="false" />
        <Column field="message" header="Message" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
    <pre v-if="job.reportJson">{{ job.reportJson }}</pre>
    <a :href="`/api/migrations/${job.id}/report`">Download report</a>
    <button class="secondary" type="button" @click="cancel">Cancel</button>
  </section>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import * as signalR from '@microsoft/signalr'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData, http } from '../api/http'
import { formatWhen } from '../utils/format'
import { useAuthStore } from '../stores/auth'
const route = useRoute()
const auth = useAuthStore()
const job = ref<any>(null)
const logs = ref<any[]>([])
let connection: signalR.HubConnection | null = null
async function load() {
  job.value = await getData(`/api/migrations/${route.params.id}`)
  logs.value = await getData(`/api/jobs/${route.params.id}`)
}
async function cancel() { await http.post(`/api/jobs/${route.params.id}/cancel?jobType=migration`) ; await load() }
onMounted(async () => {
  await load()
  connection = new signalR.HubConnectionBuilder().withUrl(`/hubs/jobs?access_token=${auth.accessToken}`).withAutomaticReconnect().build()
  connection.on('progress', async () => load())
  await connection.start()
  await connection.invoke('Subscribe', route.params.id)
})
onUnmounted(() => { connection?.stop() })
</script>
