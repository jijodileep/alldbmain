<template>
  <form class="form panel wide" @submit.prevent="save">
    <h1>{{ editing ? 'Edit schedule' : 'New schedule' }}</h1>
    <p class="page-lead">This schedule covers every discovered database on the server. Backup files are stored in a folder named after the server.</p>
    <div class="grid two">
      <label>Server
        <select v-model="form.serverId" required @change="loadServer">
          <option value="">Select</option>
          <option v-for="server in servers" :key="server.id" :value="server.id">{{ server.name }}</option>
        </select>
      </label>
      <label>Retention count <input v-model.number="form.retentionCount" type="number" min="1" max="30" required /></label>
      <label>Time zone
        <select v-model="form.timeZoneId" required>
          <option v-for="zone in timeZones" :key="zone" :value="zone">{{ zone }}</option>
        </select>
      </label>
      <label>Schedule
        <select v-model="form.scheduleKind">
          <option value="DailyTime">Daily time</option>
          <option value="Interval">Interval</option>
          <option value="Cron">Cron</option>
        </select>
      </label>
      <label v-if="form.scheduleKind === 'DailyTime'">Daily time <input v-model="form.dailyTime" type="time" required /></label>
      <label v-if="form.scheduleKind === 'Interval'">Every hours <input v-model.number="form.intervalHours" type="number" min="1" max="24" required /></label>
      <label v-if="form.scheduleKind === 'Cron'">Cron <input v-model="form.cronExpression" placeholder="0 0 2 * * ?" required /></label>
    </div>
    <div class="checks">
      <label><input v-model="form.includeRoutines" type="checkbox" /> Routines</label>
      <label><input v-model="form.includeTriggers" type="checkbox" /> Triggers</label>
      <label><input v-model="form.includeEvents" type="checkbox" /> Events</label>
      <label><input v-model="form.useSingleTransaction" type="checkbox" /> Single transaction</label>
      <label><input v-model="form.isEnabled" type="checkbox" /> Enabled</label>
    </div>
    <div class="actions">
      <button type="submit">Save schedule</button>
      <button class="secondary" type="button" :disabled="!form.serverId" @click="queue">Queue backup now</button>
      <RouterLink to="/backup-schedules"><button class="secondary" type="button">Cancel</button></RouterLink>
    </div>
    <p v-if="message">{{ message }}</p>
    <p v-if="errorText" class="error">{{ errorText }}</p>
  </form>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { apiErrorMessage, getData, postData } from '../api/http'

const route = useRoute()
const router = useRouter()
const editing = computed(() => route.path.endsWith('/edit'))
const timeZones = ref(['UTC', 'Asia/Kolkata', 'Asia/Dubai', 'Asia/Singapore', 'Europe/London', 'America/New_York'])
const servers = ref<any[]>([])
const configs = ref<any[]>([])
const message = ref('')
const errorText = ref('')
const form = reactive({
  id: '',
  serverId: '',
  retentionCount: 7,
  timeZoneId: 'UTC',
  scheduleKind: 'DailyTime',
  dailyTime: '02:00',
  intervalHours: 6,
  cronExpression: '0 0 2 * * ?',
  includeRoutines: true,
  includeTriggers: true,
  includeEvents: true,
  useSingleTransaction: true,
  isEnabled: true
})

function fill(item: any) {
  form.id = item.id
  form.serverId = item.serverId
  form.retentionCount = item.retentionCount
  form.includeRoutines = item.includeRoutines
  form.includeTriggers = item.includeTriggers
  form.includeEvents = item.includeEvents
  form.useSingleTransaction = item.useSingleTransaction
  form.isEnabled = item.isEnabled
  form.timeZoneId = item.schedule?.timeZoneId || 'UTC'
  if (!timeZones.value.includes(form.timeZoneId)) timeZones.value.push(form.timeZoneId)
  form.scheduleKind = item.schedule?.kind || 'DailyTime'
  form.dailyTime = (item.schedule?.dailyTime || '02:00').slice(0, 5)
  form.intervalHours = item.schedule?.intervalHours || 6
  form.cronExpression = item.schedule?.cronExpression || '0 0 2 * * ?'
}

function loadServer() {
  const existing = configs.value.find((item) => item.serverId === form.serverId)
  if (existing) fill(existing)
  else form.id = ''
}

async function save() {
  message.value = ''
  errorText.value = ''
  try {
    await postData('/api/backup-configurations', {
      id: form.id || null,
      serverId: form.serverId,
      includeRoutines: form.includeRoutines,
      includeTriggers: form.includeTriggers,
      includeEvents: form.includeEvents,
      useSingleTransaction: form.useSingleTransaction,
      isEnabled: form.isEnabled,
      retentionCount: form.retentionCount,
      timeZoneId: form.timeZoneId,
      scheduleKind: form.scheduleKind,
      dailyTime: form.scheduleKind === 'DailyTime' ? form.dailyTime : null,
      intervalHours: form.scheduleKind === 'Interval' ? form.intervalHours : null,
      cronExpression: form.scheduleKind === 'Cron' ? form.cronExpression : null,
      scheduleEnabled: form.isEnabled
    })
    await router.push('/backup-schedules')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}

async function queue() {
  message.value = ''
  errorText.value = ''
  try {
    const result = await postData('/api/backups', { serverId: form.serverId })
    message.value = result.message
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}

onMounted(async () => {
  servers.value = await getData('/api/servers')
  configs.value = await getData('/api/backup-configurations')
  if (!editing.value) return
  const current = configs.value.find((item) => item.id === route.params.id)
  if (current) fill(current)
  else errorText.value = 'Schedule was not found.'
})
</script>
