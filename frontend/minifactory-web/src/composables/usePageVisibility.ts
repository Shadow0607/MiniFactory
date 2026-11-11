import { onBeforeUnmount, onMounted, ref } from 'vue'

/** 頁籤是否可見（document.visibilitychange） */
export function usePageVisibility() {
  const visible = ref(typeof document === 'undefined' ? true : document.visibilityState !== 'hidden')

  function update() {
    visible.value = document.visibilityState !== 'hidden'
  }

  onMounted(() => document.addEventListener('visibilitychange', update))
  onBeforeUnmount(() => document.removeEventListener('visibilitychange', update))

  return { visible }
}
