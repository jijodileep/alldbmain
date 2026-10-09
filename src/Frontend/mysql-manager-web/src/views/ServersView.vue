<template>
  <section>
    <div class="topbar"><h1>Servers</h1><RouterLink to="/servers/create"><button type="button">Register server</button></RouterLink></div>
    <div class="panel">
      <DataGrid :rows="servers" :filter-fields="['name', 'host', 'environment', 'status']">
        <Column field="name" header="Name" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><RouterLink :to="`/servers/${data.id}`"><strong>{{ data.name }}</strong></RouterLink></template>
        </Column>
        <Column field="host" header="Host" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ data.host }}:{{ data.port }}</template>
        </Column>
        <Column field="environment" header="Environment" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.status" /></template>
        </Column>
        <Column field="credentialText" header="Credentials" sortable filter :showFilterMenu="false" />
        <Column header="Actions">
          <template #body="{ data }">
            <div class="actions">
              <RouterLink :to="`/servers/${data.id}/edit`"><button type="button" class="secondary">Edit</button></RouterLink>
              <button type="button" class="danger" @click="askDelete(data)">Delete</button>
            </div>
          </template>
        </Column>
      </DataGrid>
    </div>
    <p v-if="errorText" class="error">{{ errorText }}</p>
    <ConfirmAction v-model:open="confirmDelete" title="Remove server" message="This removes the server from this application only. Databases on that MySQL host are not dropped, and backup files already stored are kept." confirmation="DELETE" @confirm="remove" />
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Column from 'primevue/column'
import ConfirmAction from '../components/ConfirmAction.vue'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { apiErrorMessage, getData, http } from '../api/http'

const raw = ref<any[]>([])
const confirmDelete = ref(false)
const pendingId = ref('')
const errorText = ref('')
const servers = computed(() => raw.value.map((server) => ({
  ...server,
  status: server.isActive ? 'Online' : 'Offline',
  credentialText: (server.credentials ?? []).map((credential: { purpose: string; username: string }) => `${credential.purpose}: ${credential.username}`).join(', ')
})))
function askDelete(server: { id: string }) {
  errorText.value = ''
  pendingId.value = server.id
  confirmDelete.value = true
}
async function remove() {
  errorText.value = ''
  try {
    await http.delete(`/api/servers/${pendingId.value}`, { data: { confirmation: 'DELETE' } })
    raw.value = raw.value.filter((server) => server.id !== pendingId.value)
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
onMounted(async () => { raw.value = await getData('/api/servers') })
</script>
