<template>
  <section v-if="database">
    <h1>{{ database.name }}</h1>
    <p>{{ database.serverName }} · {{ database.charset }} · {{ database.collation }}</p>
    <p>Size {{ database.sizeBytes }} bytes · tables {{ database.tableCount }} · estimated rows {{ database.estimatedRows }}</p>
    <p>Last discovered {{ database.lastDiscoveredAtUtc || 'not yet' }}</p>
    <button class="secondary" type="button" @click="deactivate">Deactivate catalog entry</button>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getData, http } from '../api/http'
const route = useRoute()
const router = useRouter()
const database = ref<any>(null)
onMounted(async () => { database.value = await getData(`/api/databases/${route.params.id}`) })
async function deactivate() {
  await http.post(`/api/databases/${route.params.id}/deactivate`)
  await router.push('/databases')
}
</script>
