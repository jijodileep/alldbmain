<template>
  <section>
    <h1>Users</h1>
    <p class="page-lead">A user can belong to more than one role. The password must be at least 12 characters.</p>
    <form class="form panel" @submit.prevent="create">
      <label>Email <input v-model="form.email" type="email" required /></label>
      <label>Display name <input v-model="form.displayName" required /></label>
      <label>Password <input v-model="form.password" type="password" minlength="12" required /></label>
      <div>
        <span>Roles</span>
        <div class="checks">
          <label v-for="role in roles" :key="role.id"><input v-model="form.roleNames" type="checkbox" :value="role.name" /> {{ role.name }}</label>
        </div>
      </div>
      <button type="submit">Create user</button>
      <p v-if="errorText" class="error">{{ errorText }}</p>
    </form>
    <div class="panel" style="margin-top: 12px">
      <DataGrid :rows="rows" :filter-fields="['displayName', 'email', 'roleText']">
        <Column field="displayName" header="Name" sortable filter :showFilterMenu="false" />
        <Column field="email" header="Email" sortable filter :showFilterMenu="false" />
        <Column field="roleText" header="Roles" sortable filter :showFilterMenu="false" />
      </DataGrid>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import Column from 'primevue/column'
import { apiErrorMessage, getData, postData } from '../api/http'
import DataGrid from '../components/DataGrid.vue'

const users = ref<any[]>([])
const roles = ref<any[]>([])
const errorText = ref('')
const form = reactive({ email: '', displayName: '', password: '', roleNames: [] as string[] })
const rows = computed(() => users.value.map((user) => ({ ...user, roleText: (user.roles ?? []).join(', ') })))
async function create() {
  errorText.value = ''
  if (!form.roleNames.length) {
    errorText.value = 'Select at least one role.'
    return
  }
  try {
    await postData('/api/users', form)
    form.email = ''
    form.displayName = ''
    form.password = ''
    form.roleNames = []
    users.value = await getData('/api/users')
  } catch (error) {
    errorText.value = apiErrorMessage(error)
  }
}
onMounted(async () => {
  users.value = await getData('/api/users')
  roles.value = await getData('/api/roles')
})
</script>
