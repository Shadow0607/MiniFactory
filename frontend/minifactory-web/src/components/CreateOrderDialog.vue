<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { createOrder } from '@/api/orders'
import { ApiError } from '@/api/errors'
import {
  MAX_LINE_ID_LENGTH,
  MAX_ORDER_QUANTITY,
  mapServerFieldErrors,
  type OrderFormField,
} from '@/constants/order'
import { useEquipmentStore } from '@/stores/equipment'

const visible = defineModel<boolean>({ required: true })
const emit = defineEmits<{ created: [orderId: string] }>()

const router = useRouter()
const equipment = useEquipmentStore()

const formRef = ref<FormInstance>()
const submitting = ref(false)

const form = reactive({
  orderNo: '',
  productCode: '',
  quantity: 1 as number | undefined,
  lineId: '',
  idempotencyKey: '',
})

/** 後端回傳的欄位錯誤（400），顯示在對應欄位下方 */
const serverErrors = ref<Partial<Record<OrderFormField, string>>>({})

// 規格（api.md §3.1）：orderNo 1～50 字，僅 A-Z a-z 0-9 - _【待確認格式】
const ORDER_NO_PATTERN = /^[A-Za-z0-9_-]+$/
// Idempotency-Key：1～128 可見 ASCII
const IDEMPOTENCY_KEY_PATTERN = /^[\x21-\x7E]{1,128}$/

/**
 * 沒填 Key 時自動產生（沿用規格範例格式，architecture.md §15）；同一訂單編號重送會得到相同結果。
 * 後端 orderNo 區分大小寫，Key 必須保留原大小寫，否則 ORD-1 與 ord-1 會誤判為 idempotency-conflict。
 * orderNo 上限 50 字 + 前綴 13 字 = 63 字，不會超過 Key 的 128 字上限；
 * 若輸入尚未通過驗證而超長，回傳空字串（不送 Key，由格式驗證擋下）。
 */
const autoKey = computed(() => {
  const orderNo = form.orderNo.trim()
  if (!orderNo) return ''
  const key = `create-order-${orderNo}`
  return key.length <= 128 ? key : ''
})

/** 文字欄位先 trim 再驗證（與送出值一致）：前後空白不會造成格式錯誤，純空白視為未填 */
function trimmedTextValidator(opts: {
  requiredMessage: string
  max?: number
  maxMessage?: string
  pattern?: RegExp
  patternMessage?: string
}) {
  return (_rule: unknown, value: unknown, callback: (error?: Error) => void) => {
    const text = typeof value === 'string' ? value.trim() : ''
    if (text === '') callback(new Error(opts.requiredMessage))
    else if (opts.max !== undefined && text.length > opts.max) callback(new Error(opts.maxMessage))
    else if (opts.pattern && !opts.pattern.test(text)) callback(new Error(opts.patternMessage))
    else callback()
  }
}

const rules: FormRules = {
  orderNo: [
    {
      validator: trimmedTextValidator({
        requiredMessage: '請輸入訂單編號',
        max: 50,
        maxMessage: '訂單編號最多 50 字',
        pattern: ORDER_NO_PATTERN,
        patternMessage: '訂單編號只能包含英文字母、數字、- 與 _',
      }),
      trigger: 'blur',
    },
  ],
  productCode: [
    {
      validator: trimmedTextValidator({
        requiredMessage: '請輸入產品代碼',
        max: 50,
        maxMessage: '產品代碼最多 50 字',
      }),
      trigger: 'blur',
    },
  ],
  quantity: [
    {
      validator: (_rule, value: unknown, callback) => {
        if (typeof value !== 'number' || !Number.isInteger(value) || value <= 0) {
          callback(new Error('數量必須是大於 0 的整數'))
        } else if (value > MAX_ORDER_QUANTITY) {
          callback(new Error(`數量不可超過 ${MAX_ORDER_QUANTITY}`))
        } else {
          callback()
        }
      },
      trigger: 'change',
    },
  ],
  lineId: [
    {
      validator: trimmedTextValidator({
        requiredMessage: '請選擇或輸入產線代號',
        max: MAX_LINE_ID_LENGTH,
        maxMessage: `產線代號最多 ${MAX_LINE_ID_LENGTH} 字`,
      }),
      trigger: 'change',
    },
  ],
  idempotencyKey: [
    {
      validator: (_rule, value: string, callback) => {
        const key = (value ?? '').trim()
        if (key === '' || IDEMPOTENCY_KEY_PATTERN.test(key)) callback()
        else callback(new Error('Idempotency-Key 需為 1～128 個可見 ASCII 字元（不含空白）'))
      },
      trigger: 'blur',
    },
  ],
}

// 修改欄位後清掉該欄位的伺服器錯誤
// 只清除「被修改的那個欄位」的伺服器錯誤，其他欄位的錯誤保留
watch(
  () => ({ ...form }),
  (current, previous) => {
    if (Object.keys(serverErrors.value).length === 0) return
    const remaining = { ...serverErrors.value }
    for (const field of Object.keys(remaining) as OrderFormField[]) {
      if (current[field] !== previous[field]) delete remaining[field]
    }
    serverErrors.value = remaining
  },
)

// 開啟時重置表單，並載入產線候選值（失敗不提示，仍可手動輸入）
watch(visible, (isVisible) => {
  if (!isVisible) return
  form.orderNo = ''
  form.productCode = ''
  form.quantity = 1
  form.lineId = ''
  form.idempotencyKey = ''
  serverErrors.value = {}
  formRef.value?.clearValidate()
  if (!equipment.loaded) void equipment.fetchAll(true)
})

/** 後端欄位名（含 Idempotency-Key 標頭名）依對照表轉成表單欄位，錯誤才會顯示在對應欄位下 */
function applyFieldErrors(error: ApiError) {
  serverErrors.value = mapServerFieldErrors(error.fieldErrors)
}

async function submit() {
  if (submitting.value) return // 防止重複送出
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  submitting.value = true
  try {
    const key = form.idempotencyKey.trim() || autoKey.value
    const { data, replayed } = await createOrder(
      {
        orderNo: form.orderNo.trim(),
        productCode: form.productCode.trim(),
        quantity: form.quantity as number,
        lineId: form.lineId.trim(),
      },
      key || undefined,
    )

    if (replayed) {
      ElMessage.info('此請求先前已送出過，已回傳第一次建立的結果')
    } else {
      ElMessage.success(`訂單 ${data.orderNo} 已建立`)
    }
    if (data.dispatchStatus === 'Pending') {
      // Pending 是 Outbox 的正常中間狀態（Phase 2.3 前固定如此），不是故障，用 info 等級
      ElMessage.info('訂單已建立，派工事件已排入 Outbox，等待派送')
    }

    visible.value = false
    emit('created', data.orderId)
    await router.push({ name: 'order-detail', params: { id: data.orderId } })
  } catch (e) {
    // 錯誤提示已由攔截器顯示；這裡只把 400 的欄位錯誤標在表單上
    if (e instanceof ApiError && e.status === 400) applyFieldErrors(e)
    else if (!(e instanceof ApiError)) throw e
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="visible"
    title="建立訂單"
    width="520px"
    :close-on-click-modal="false"
    :close-on-press-escape="!submitting"
    :show-close="!submitting"
  >
    <el-form
      ref="formRef"
      :model="form"
      :rules="rules"
      label-width="130px"
      :disabled="submitting"
      @submit.prevent="submit"
    >
      <el-form-item label="訂單編號" prop="orderNo" :error="serverErrors.orderNo">
        <el-input v-model="form.orderNo" maxlength="50" placeholder="例：ORD-20260921-001" />
      </el-form-item>
      <el-form-item label="產品代碼" prop="productCode" :error="serverErrors.productCode">
        <el-input v-model="form.productCode" maxlength="50" placeholder="例：Motor-A" />
      </el-form-item>
      <el-form-item label="數量" prop="quantity" :error="serverErrors.quantity">
        <el-input-number
          v-model="form.quantity"
          :min="1"
          :max="MAX_ORDER_QUANTITY"
          :precision="0"
          :step="1"
        />
      </el-form-item>
      <el-form-item label="產線" prop="lineId" :error="serverErrors.lineId">
        <el-select
          v-model="form.lineId"
          filterable
          allow-create
          default-first-option
          placeholder="選擇或輸入產線代號，例：Line-01"
          style="width: 100%"
        >
          <el-option v-for="id in equipment.lineIds" :key="id" :label="id" :value="id" />
        </el-select>
      </el-form-item>
      <el-form-item
        label="Idempotency-Key"
        prop="idempotencyKey"
        :error="serverErrors.idempotencyKey"
      >
        <el-input
          v-model="form.idempotencyKey"
          maxlength="128"
          :placeholder="autoKey || '留空則自動產生'"
        />
        <div class="field-hint">選填；留空會自動產生，重複送出同一筆訂單時不會建立兩張。</div>
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="submitting" :disabled="submitting" @click="submit">
        建立訂單
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.field-hint {
  margin-top: 4px;
  font-size: 12px;
  line-height: 1.5;
  color: var(--el-text-color-secondary);
}
</style>
