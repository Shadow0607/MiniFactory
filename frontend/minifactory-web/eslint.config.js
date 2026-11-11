import pluginVue from 'eslint-plugin-vue'
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'

export default defineConfigWithVueTs(
  { ignores: ['dist/**', 'node_modules/**', 'coverage/**'] },
  pluginVue.configs['flat/essential'],
  vueTsConfigs.recommended,
  {
    rules: {
      // 安全規範：禁止以 v-html 輸出使用者輸入或 API 回傳內容
      'vue/no-v-html': 'error',
      'vue/multi-word-component-names': 'off',
    },
  },
)
