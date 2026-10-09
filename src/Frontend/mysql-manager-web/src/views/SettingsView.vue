<template>
  <section>
    <h1>Settings</h1>
    <article v-if="settings" class="panel">
      <p>Daily retention: {{ settings.dailyRetentionDays }}</p>
      <p>Minimum free disk: {{ settings.minimumFreeDiskPercentage }}%</p>
      <p>Encryption: {{ settings.encryptionEnabled }}</p>
      <p>Git enabled: {{ settings.gitEnabled }}</p>
      <p>Allow history rewrite: {{ settings.allowHistoryRewrite }}</p>
      <p>Storage: {{ settings.storageType }}</p>
      <p>Max concurrent backups: {{ settings.maxConcurrentBackups }}</p>
      <p>Max concurrent copies: {{ settings.maxConcurrentMigrations }}</p>
    </article>
    <button type="button" @click="rotate">Rotate encryption key</button>
    <p v-if="message">{{ message }}</p>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getData, postData } from '../api/http'
const settings = ref<any>(null)
const message = ref('')
async function rotate() { const result = await postData<string>('/api/settings/encryption-keys/rotate'); message.value = result.data }
onMounted(async () => { settings.value = await getData('/api/settings') })
</script>
