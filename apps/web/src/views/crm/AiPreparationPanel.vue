<template>
  <section class="ai-prep">
    <div class="head"><strong>AI 准备包（本地模拟）</strong><el-button size="small" @click="load">刷新版本</el-button></div>
    <p class="hint">从身份依据、已完成技术报告或已审核内容观察生成待审参考草稿。当前只使用本地模拟器，不调用外部 AI、不自动发送客户。</p>
    <div v-if="canManage" class="controls">
      <el-select v-model="taskType" style="width:160px"><el-option label="企业画像" value="company_profile" /><el-option label="网站诊断摘要" value="diagnostic_summary" /><el-option label="破冰话题" value="icebreaker" /></el-select>
      <el-input-number v-model="maxCostCents" :min="0" :max="100000" />
      <span class="hint">模拟预算单位，0 可测试超限</span>
      <el-button type="primary" size="small" :loading="busy" @click="generate">生成新版本</el-button>
    </div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <el-empty v-if="!items.length" description="暂无准备包版本" :image-size="54" />
    <el-card v-for="item in items" :key="item.id" shadow="never" class="version">
      <div><strong>{{ label(item.taskType) }} V{{ item.version }}</strong> · {{ status(item.status) }} · {{ item.providerCode }} · {{ formatTime(item.createdAt) }}</div>
      <div class="hint">证据 {{ item.evidence.map(e => `${e.type}:${e.id}`).join('、') || '无' }} · 估算 {{ item.estimatedCostCents }} / 上限 {{ item.maxCostCents }} / 实际 {{ item.actualCostCents }}</div>
      <p v-for="(claim, index) in item.claims" :key="index">{{ claim.text }} <small>证据 {{ claim.evidenceType }}:{{ claim.evidenceId }}</small></p>
      <p v-if="item.failureReason" class="hint">{{ item.failureReason }}</p>
      <p v-if="item.reviewReason" class="hint">审核：{{ item.reviewReason }}（{{ item.reviewedBy }}，{{ formatTime(item.reviewedAt) }}）</p>
      <div v-if="item.status === 'draft' && canManage && canReview"><el-button size="small" type="success" @click="review(item, 'approved')">批准草稿</el-button><el-button size="small" type="danger" @click="review(item, 'rejected')">驳回草稿</el-button></div>
    </el-card>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { apiGet, apiPost } from '../../api/http';

type Claim = { text: string; evidenceType: string; evidenceId: string };
type Draft = { id: string; version: number; taskType: string; providerCode: string; status: string; evidence: Array<{type:string;id:string}>; claims: Claim[]; estimatedCostCents: number; maxCostCents: number; actualCostCents: number; failureReason?: string; createdAt: string; reviewedAt?: string; reviewedBy?: string; reviewReason?: string };
const props = defineProps<{ candidateId: string; canManage: boolean; canReview: boolean }>();
const items = ref<Draft[]>([]);
const taskType = ref('company_profile');
const maxCostCents = ref(1);
const busy = ref(false);
const error = ref('');
const base = () => `/api/v1/crm/identity-candidates/${props.candidateId}/ai-preparations`;
const label = (value: string) => ({company_profile:'企业画像',diagnostic_summary:'网站诊断摘要',icebreaker:'破冰话题'}[value] || value);
const status = (value: string) => ({unsupported:'缺少依据',cost_limit:'预算超限',failed:'生成失败',draft:'待人工审核',approved:'已批准',rejected:'已驳回'}[value] || value);
const formatTime = (value?: string) => value ? new Date(value).toLocaleString('zh-CN') : '';
async function load() { try { const result = await apiGet<{items:Draft[]}>(base()); items.value = result.items || []; error.value = ''; } catch (e) { error.value = String(e); } }
async function generate() { busy.value = true; try { await apiPost(base(), { requestKey: crypto.randomUUID(), taskType: taskType.value, maxCostCents: maxCostCents.value }); ElMessage.success('已保存模拟版本'); await load(); } catch (e) { error.value = String(e); } finally { busy.value = false; } }
async function review(item: Draft, decision: 'approved' | 'rejected') { try { const value = await ElMessageBox.prompt('填写审核理由（至少 10 字）', decision === 'approved' ? '批准草稿' : '驳回草稿', { inputValidator: text => text.trim().length >= 10 || '至少填写 10 字' }); await apiPost(`${base()}/${item.id}/review`, { decision, reason: value.value.trim() }); ElMessage.success('审核已保存'); await load(); } catch (e) { if (e !== 'cancel' && e !== 'close') error.value = String(e); } }
onMounted(load);
</script>

<style scoped>
.ai-prep { margin: 16px 0; padding: 12px; border: 1px solid var(--el-border-color); border-radius: 6px; }
.head,.controls { display:flex; align-items:center; gap:8px; flex-wrap:wrap; }
.head { justify-content:space-between; }
.version { margin-top:8px; }
.hint,small { color:var(--el-text-color-secondary); font-size:12px; }
</style>
