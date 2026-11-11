<script setup lang="ts">
import { ref, watch } from 'vue'
import { useSystemStore } from '@/stores/system'

const system = useSystemStore()
const input = ref('')

// 每次開啟都清空輸入，避免殘留上一次輸入的 Key
watch(
  () => system.apiKeyDialogVisible,
  (visible) => {
    if (visible) input.value = ''
  },
)

function submit() {
  system.submitApiKey(input.value)
  input.value = ''
}

function cancel() {
  system.cancelApiKeyPrompt()
  input.value = ''
}
</script>

<template>
  <el-dialog
    :model-value="system.apiKeyDialogVisible"
    title="輸入 Operator API Key"
    width="440px"
    :close-on-click-modal="false"
    @close="cancel"
  >
    <p class="hint">
      建立訂單、告警結案等寫入操作需要 Operator API Key。Key 只會存在這個分頁的 sessionStorage，關閉分頁即清除，不會寫入程式碼或回傳到其他地方。
    </p>
    <el-input
      v-model="input"
      type="password"
      show-password
      autocomplete="off"
      placeholder="請輸入 Operator API Key"
      @keyup.enter="submit"
    />
    <template #footer>
      <el-button @click="cancel">取消</el-button>
      <el-button type="primary" :disabled="input.trim() === ''" @click="submit">確定</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.hint {
  margin-top: 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}
</style>
