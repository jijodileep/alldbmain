<template>
  <section>
    <h1>Job logs</h1>
    <div class="panel">
      <DataGrid :rows="logs" :filter-fields="['level', 'message']">
        <Column field="createdAtUtc" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.createdAtUtc) }}</template>
        </Column>
        <Column field="level" header="Level" sortable filter :showFilterMenu="false" />
        <Column field="message" header="Message" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import { getData } from '../api/http'
import { formatWhen } from '../utils/format'

const route = useRoute()
const logs = ref<any[]>([])
onMounted(async () => { logs.value = await getData(`/api/jobs/${route.params.id}`) })
</script>
