<template>
  <section>
    <h1>Git upload</h1>
    <p class="page-lead">One repository receives every backup. Each server is uploaded into its own folder: <code>server-name/database-name/daily/</code>.</p>
    <p class="warning">Deleting a file from the working tree does not remove it from Git history. History purge is off unless Allow Git history rewrite is enabled. After a purge, older backup versions become permanently unrecoverable.</p>
    <form class="form panel" @submit.prevent="save">
      <label>Name <input v-model="form.name" required /></label>
      <label>Remote URL <input v-model="form.remoteUrl" placeholder="https://github.com/org/backups.git" required /></label>
      <label>Branch <input v-model="form.branch" required /></label>
      <label>Mode <select v-model="form.mode"><option value="MetadataOnly">Metadata only</option><option value="BackupFilesWithLfs">Backup files + Git LFS</option><option value="RollingRepository">Rolling repository</option></select></label>
      <label>Auth <select v-model="form.authType"><option value="HttpsPat">HTTPS token</option><option value="SshDeployKey">SSH deploy key</option></select></label>
      <label>Secret <textarea v-model="form.secret" rows="3" :placeholder="form.id ? 'Leave blank to keep the saved secret' : ''" /></label>
      <label><input v-model="form.allowHistoryRewrite" type="checkbox" /> Allow Git history rewrite</label>
      <button type="submit">Save repository</button>
    </form>
    <p v-if="message">{{ message }}</p>
    <p v-if="errorText" class="error">{{ errorText }}</p>
    <div class="panel">
      <DataGrid :rows="repos" :filter-fields="['name', 'remoteUrl', 'branch', 'mode', 'folder', 'lastError']">
        <Column field="name" header="Name" sortable filter :showFilterMenu="false" />
        <Column field="remoteUrl" header="Remote" sortable filter :showFilterMenu="false" />
        <Column field="branch" header="Branch" sortable filter :showFilterMenu="false" />
        <Column field="mode" header="Mode" sortable filter :showFilterMenu="false" />
        <Column field="folder" header="Server folder" sortable filter :showFilterMenu="false" />
        <Column field="lastPushAtUtc" header="Last push" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.lastPushAtUtc) || 'Never' }}</template>
        </Column>
        <Column field="lastError" header="Error" sortable filter :showFilterMenu="false">
          <template #body="{ data }"><span class="error-text">{{ data.lastError }}</span></template>
        </Column>
        <Column header="">
          <template #body="{ data }">
            <div class="actions">
              <button type="button" @click="test(data.id)">Test</button>
              <button class="secondary" type="button" @click="history(data.id)">History</button>
              <button class="danger" type="button" @click="purgeId = data.id">Purge</button>
            </div>
          </template>
        </Column>
      </DataGrid>
    </div>
    <div v-if="historyLoaded" class="panel" style="margin-top: 12px">
      <h2>History</h2>
      <p v-if="!log.length">This branch has no commits yet.</p>
      <DataGrid v-else :rows="log" :filter-fields="['sha', 'subject']">
        <Column field="committedAtUtc" header="Time" sortable filter :showFilterMenu="false">
          <template #body="{ data }">{{ formatWhen(data.committedAtUtc) }}</template>
        </Column>
        <Column field="sha" header="Commit" sortable filter :showFilterMenu="false" />
        <Column field="subject" header="Subject" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
    <ConfirmAction v-model:open="purgeOpen" title="Purge Git history" message="Older Git backup versions will become permanently unrecoverable after repository purge." confirmation="PURGE" @confirm="purge" />
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import Column from 'primevue/column'
import ConfirmAction from '../components/ConfirmAction.vue'
import DataGrid from '../components/DataGrid.vue'
import { apiErrorMessage, getData, postData } from '../api/http'
import { formatWhen } from '../utils/format'

const repos = ref<any[]>([])
const log = ref<any[]>([])
const historyLoaded = ref(false)
const message = ref('')
const errorText = ref('')
const purgeId = ref('')
const purgeOpen = computed({ get: () => purgeId.value !== '', set: (value) => { if (!value) purgeId.value = '' } })
const form = reactive({ id: '', name: '', remoteUrl: '', branch: 'main', mode: 'BackupFilesWithLfs', authType: 'HttpsPat', secret: '', allowHistoryRewrite: false, isEnabled: true, secretProvider: 'LocalEncrypted' })

function applyRepo(repo: any) {
  form.id = repo.id
  form.name = repo.name
  form.remoteUrl = repo.remoteUrl
  form.branch = repo.branch
  form.mode = repo.mode
  form.authType = repo.authType
  form.allowHistoryRewrite = repo.allowHistoryRewrite
}

async function load() {
  const rows = await getData<any[]>('/api/git-repositories')
  repos.value = rows.filter((repo) => repo.isEnabled).map((repo) => ({ ...repo, folder: 'server-name/database-name/daily' }))
  const current = rows.find((repo) => repo.isEnabled) ?? rows[0]
  if (current) applyRepo(current)
}

async function save() {
  errorText.value = ''
  try {
    await postData('/api/git-repositories', { ...form, id: form.id || null, serverId: null })
    form.secret = ''
    message.value = 'One Git repository is saved. Each server is uploaded into its own folder.'
    await load()
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function test(id: string) {
  errorText.value = ''
  try {
    const response = await postData<any>(`/api/git-repositories/${id}/test`)
    const result = response.data
    if (result?.authenticated) {
      message.value = `Connected to ${result.repository} on branch ${result.branch}.`
    } else {
      errorText.value = result?.error || 'Git connection failed.'
    }
    await load()
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function history(id: string) {
  errorText.value = ''
  try {
    const rows = await getData<any[]>(`/api/git-repositories/${id}/history`)
    log.value = rows.map((entry) => ({ ...entry, id: entry.sha }))
    historyLoaded.value = true
    message.value = rows.length ? '' : `Branch ${form.branch} has no commits yet.`
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
async function purge() { if (purgeId.value) await postData(`/api/git-repositories/${purgeId.value}/purge`, { confirmation: 'PURGE' }) }
onMounted(load)
</script>
