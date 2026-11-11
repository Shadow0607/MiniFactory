import { h } from 'vue'
import { ElMessage, ElNotification } from 'element-plus'
import { describeApiError, type ApiError } from './errors'

/** 把 ApiError 顯示成 Element Plus 提示；內容一律當純文字（VNode 文字節點），不使用 HTML 字串 */
export function notifyApiError(error: ApiError): void {
  const { title, lines } = describeApiError(error)

  // 400 欄位驗證錯誤：逐欄位列出
  if (error.status === 400 && error.hasFieldErrors) {
    ElNotification({
      title,
      type: 'error',
      duration: 8000,
      message: h(
        'div',
        lines.map((line) => h('div', line)),
      ),
    })
    return
  }

  ElMessage({
    type: error.status === 401 ? 'warning' : 'error',
    showClose: true,
    duration: 6000,
    message: h(
      'div',
      lines.map((line) => h('div', line)),
    ),
  })
}
