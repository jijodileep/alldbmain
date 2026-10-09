<template>
  <DataTable
    :value="rows"
    paginator
    :rows="10"
    :rowsPerPageOptions="[10, 25, 50, 100]"
    stripedRows
    sortMode="multiple"
    removableSort
    size="small"
    dataKey="id"
    :filters="filters"
    filterDisplay="row"
    :globalFilterFields="fields"
    paginatorTemplate="RowsPerPageDropdown FirstPageLink PrevPageLink CurrentPageReport NextPageLink LastPageLink"
    currentPageReportTemplate="{first}–{last} of {totalRecords}"
  >
    <template #header>
      <div class="table-toolbar">
        <span>{{ rows.length }} rows</span>
        <input v-model="filters.global.value" type="search" placeholder="Search this list" />
      </div>
    </template>
    <template #empty>No rows match these filters.</template>
    <slot />
  </DataTable>
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import DataTable from 'primevue/datatable'

const props = defineProps<{ rows: any[]; filterFields?: string[] }>()
const bag = reactive<Record<string, { value: string | null; matchMode: string }>>({
  global: { value: null, matchMode: 'contains' }
})
const filters = new Proxy(bag, {
  get(target, prop, receiver) {
    if (typeof prop === 'string' && !(prop in target) && !prop.startsWith('__v')) {
      target[prop] = { value: null, matchMode: 'contains' }
    }
    return Reflect.get(target, prop, receiver)
  }
})
const fields = computed(() => {
  const fromRows = props.rows[0] ? Object.keys(props.rows[0]) : []
  return [...new Set([...(props.filterFields ?? []), ...fromRows])].filter((field) => field !== 'id')
})
</script>
