<template>
  <section>
    <h1>Audit log</h1>
    <div class="panel">
      <DataGrid :rows="rows" :filter-fields="['action', 'result', 'sourceModification', 'details']">
        <Column field="timestamp" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.timestamp) }}</template>
        </Column>
        <Column field="action" header="Action" sortable filter :showFilterMenu="false" />
        <Column field="result" header="Result" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><StatusBadge :value="data.result" /></template>
        </Column>
        <Column field="sourceModification" header="Source modification" sortable filter :showFilterMenu="false" />
        <Column field="details" header="Details" sortable filter :showFilterMenu="false" style="min-width: 16rem" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

const rows = ref<any[]>([])
onMounted(async () => { const page = await getData<any>('/api/audit-logs'); rows.value = page.items })
</script>
