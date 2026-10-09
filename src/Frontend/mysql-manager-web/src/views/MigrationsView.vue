<template>
  <section>
    <div class="topbar">
      <div>
        <h1>Copy jobs</h1>
        <p class="page-lead">Each database is copied into a new destination. The source is not modified.</p>
      </div>
      <RouterLink to="/migrations/create"><button type="button">Copy databases</button></RouterLink>
    </div>
    <div class="panel">
      <DataGrid :rows="jobs" :filter-fields="['sourceServerName', 'sourceDatabaseName', 'destinationServerName', 'destinationDatabaseName', 'mode', 'status', 'validationStatus', 'errorMessage']">
        <Column field="sourceServerName" header="Source server" sortable filter :showFilterMenu="false" />
        <Column field="sourceDatabaseName" header="Source database" sortable filter :showFilterMenu="false" />
        <Column field="destinationServerName" header="Destination server" sortable filter :showFilterMenu="false" />
        <Column field="destinationDatabaseName" header="Destination database" sortable filter :showFilterMenu="false" />
        <Column field="mode" header="Mode" sortable filter :showFilterMenu="false" />
        <Column field="status" header="Status" sortable filter :showFilterMenu="false">
          <template #body="{ data }">
            <RouterLink :to="`/migrations/${data.id}`"><StatusBadge :value="data.status" /></RouterLink>
          </template>
        </Column>
        <Column field="validationStatus" header="Validation" sortable filter :showFilterMenu="false" />
        <Column field="errorMessage" header="Error" sortable filter :showFilterMenu="false" style="min-width: 16rem">
          <template #body="{ data }"><span class="error-text">{{ data.errorMessage }}</span></template>
        </Column>
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

const jobs = ref<any[]>([])
onMounted(async () => { jobs.value = await getData('/api/migrations') })
</script>
