<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { ApiError } from '@/api/errors'
import type { Alert } from '@/api/types'
import { useAlertsStore } from '@/stores/alerts'

const props = defineProps<{ alert: Alert | null }>()
const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ resolved: [alert: Alert] }>()

const alerts = useAlertsStore()
const note = ref('')
const submitting = ref(false)

watch(visible, (isVisible) => {
  if (isVisible) note.value = ''
})

async function submit() {
  if (!props.alert || submitting.value) return // 防止重複送出
  submitting.value = true
  try {
    const resolved = await alerts.resolve(props.alert.id, note.value)
    ElMessage.success('告警已結案')
    visible.value = false
    emit('resolved', resolved)
  } catch (e) {
    // 錯誤提示已由攔截器顯示（401 會清除 Key，下次送出會重新要求輸入）
    if (!(e instanceof ApiError)) throw e
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    title="告警結案"
    width="480px"
    :close-on-click-modal="false"
    :show-close="!submitting"
  >
    <template v-if="props.alert">
      <p class="alert-message">{{ props.alert.message ?? props.alert.reason }}</p>
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="結案只會關閉這則告警，不會改變工序、訂單或設備的狀態。若問題條件仍存在（例如設備仍離線），系統不會重新建立告警，直到下次狀態再發生轉換。"
      />
      <el-input
        v-model="note"
        type="textarea"
        :rows="3"
        maxlength="500"
        show-word-limit
        placeholder="處置備註（選填，最多 500 字）"
        class="note-input"
        :disabled="submitting"
      />
    </template>
    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="submitting" :disabled="submitting" @click="submit">
        確認結案
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.alert-message {
  margin-top: 0;
  font-weight: 600;
}

.note-input {
  margin-top: 16px;
}
</style>
