<template>
  <section>
    <h1>Notifications</h1>
    <form class="form panel" @submit.prevent="save">
      <label>Channel <select v-model="form.channel"><option>Email</option><option>Webhook</option></select></label>
      <label>Name <input v-model="form.name" /></label>
      <label>Destination <input v-model="form.destination" /></label>
      <label>Events <input v-model="form.eventTypesCsv" placeholder="BACKUP_SUCCESS,BACKUP_FAILURE" /></label>
      <button type="submit">Save endpoint</button>
    </form>
    <p v-if="errorText" class="error">{{ errorText }}</p>
    <div class="panel" style="margin-top: 12px">
      <DataGrid :rows="items" :filter-fields="['eventType', 'channel', 'status', 'recipient', 'error']">
        <Column field="createdAtUtc" header="Created" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="eventType" header="Event" sortable filter :showFilterMenu="false" />
        <Column field="channel" header="Channel" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.status" /></template>
        </Column>
        <Column field="recipient" header="Recipient" sortable filter :showFilterMenu="false" />
        <Column field="error" header="Error" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><span class="error-text">{{ data.error }}</span></template>
        </Column>
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import Column from 'primevue/column'
import { apiErrorMessage, getData, postData } from '../api/http'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { formatWhen } from '../utils/format'
const items = ref<any[]>([])
const errorText = ref('')
const form = reactive({ channel: 'Email', name: '', destination: '', isEnabled: true, eventTypesCsv: '' })
async function save() {
  errorText.value = ''
  try {
    await postData('/api/notifications/endpoints', form)
    items.value = await getData('/api/notifications')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
onMounted(async () => { items.value = await getData('/api/notifications') })
</script>
