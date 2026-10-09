<template>
  <section>
    <h1>Roles</h1>
    <p class="page-lead">Every role and the permissions assigned to it.</p>
    <div class="panel">
      <DataGrid :rows="rows" :filter-fields="['name', 'description', 'permissionText']">
        <Column field="name" header="Role" sortable filter :showFilterMenu="false" />
        <Column field="description" header="Description" sortable filter :showFilterMenu="false" />
        <Column field="permissionText" header="Permissions" sortable filter :showFilterMenu="false" style="min-width: 18rem" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Column from 'primevue/column'
import DataGrid from '../components/DataGrid.vue'
import { getData } from '../api/http'

const roles = ref<any[]>([])
const rows = computed(() => roles.value.map((role) => ({ ...role, permissionText: (role.permissions ?? []).join(', ') })))
onMounted(async () => { roles.value = await getData('/api/roles') })
</script>
