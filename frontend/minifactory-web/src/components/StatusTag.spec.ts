// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it } from 'vitest'
import StatusTag from './StatusTag.vue'

function render(kind: 'order' | 'job' | 'execution' | 'equipment' | 'severity' | 'alert', value: string) {
  return mount(StatusTag, { props: { kind, value }, global: { plugins: [ElementPlus] } })
}

describe('StatusTag', () => {
  it('訂單狀態顯示中文標籤與對應顏色', () => {
    const done = render('order', 'Completed')
    expect(done.text()).toBe('已完成')
    expect(done.find('.el-tag').classes()).toContain('el-tag--success')

    const review = render('order', 'NeedsReview')
    expect(review.text()).toBe('需人工檢視')
    expect(review.find('.el-tag').classes()).toContain('el-tag--danger')
  })

  it('依 kind 使用不同對照表', () => {
    expect(render('equipment', 'Offline').text()).toBe('離線')
    expect(render('severity', 'Critical').text()).toBe('嚴重')
    expect(render('alert', 'Open').text()).toBe('未處理')
  })

  it('未知狀態退回原字串與 info 顏色，不讓畫面壞掉', () => {
    const tag = render('order', 'Brand-New')
    expect(tag.text()).toBe('Brand-New')
    expect(tag.find('.el-tag').classes()).toContain('el-tag--info')
  })

  it('以純文字輸出，不解析 HTML', () => {
    const tag = render('order', '<b>x</b>')
    expect(tag.find('b').exists()).toBe(false)
    expect(tag.text()).toBe('<b>x</b>')
  })
})
