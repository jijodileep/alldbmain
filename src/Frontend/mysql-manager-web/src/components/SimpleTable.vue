<template>
  <div class="panel">
    <table>
      <thead>
        <tr>
          <th v-for="column in columns" :key="column">{{ column }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(row, index) in rows" :key="index">
          <td v-for="column in columns" :key="column"><slot :name="column" :row="row">{{ row[column] }}</slot></td>
        </tr>
        <tr v-if="rows.length === 0"><td :colspan="columns.length">No records.</td></tr>
      </tbody>
    </table>
  </div>
</template>

<script setup lang="ts">
defineProps<{ columns: string[]; rows: Record<string, unknown>[] }>()
</script>
