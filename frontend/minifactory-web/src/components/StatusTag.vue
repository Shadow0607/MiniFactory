<script setup lang="ts">
import { computed } from 'vue'
import {
  alertStatusMeta,
  equipmentStatusMeta,
  executionStatusMeta,
  jobStatusMeta,
  orderStatusMeta,
  resolveMeta,
  severityMeta,
  type StatusMeta,
} from '@/utils/status'

const props = defineProps<{
  kind: 'order' | 'job' | 'execution' | 'equipment' | 'severity' | 'alert'
  value: string
}>()

const tables: Record<typeof props.kind, Record<string, StatusMeta>> = {
  order: orderStatusMeta,
  job: jobStatusMeta,
  execution: executionStatusMeta,
  equipment: equipmentStatusMeta,
  severity: severityMeta,
  alert: alertStatusMeta,
}

const meta = computed(() => resolveMeta(tables[props.kind], props.value))
</script>

<template>
  <el-tag :type="meta.type" effect="light">{{ meta.label }}</el-tag>
</template>
