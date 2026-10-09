<template>
  <section v-if="file">
    <h1>{{ file.fileName }}</h1>
    <StatusBadge :value="file.status" />
    <StatusBadge :value="file.verificationStatus" />
    <p>Size {{ file.sizeBytes }} · compressed {{ file.compressedSizeBytes }}</p>
    <p>Checksum {{ file.checksum }}</p>
    <p>Key {{ file.encryptionKeyId }} · {{ file.encryptionAlgorithm }}</p>
    <p>Storage {{ file.storagePath }}</p>
    <p>MySQL {{ file.mySqlVersion || 'recorded at backup time' }}</p>
    <button class="danger" type="button" @click="open = true">Delete backup file</button>
    <ConfirmAction v-model:open="open" title="Delete backup" message="This deletes the backup artifact from application storage. It does not touch the source database." confirmation="DELETE" @confirm="remove" />
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ConfirmAction from '../components/ConfirmAction.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { getData, http } from '../api/http'
const route = useRoute()
const router = useRouter()
const file = ref<any>(null)
const open = ref(false)
onMounted(async () => { file.value = await getData(`/api/backups/${route.params.id}`) })
async function remove() {
  await http.delete(`/api/backups/${route.params.id}`, { data: { confirmation: 'DELETE' } })
  await router.push('/backups')
}
</script>
