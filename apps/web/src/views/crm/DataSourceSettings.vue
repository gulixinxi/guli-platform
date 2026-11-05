<template>
  <div class="source-settings">
    <el-card shadow="never">
      <template #header><div class="heading"><strong>数据源设置</strong><el-button :loading="loading" @click="refresh">刷新状态</el-button></div></template>
      <el-alert title="这里统一管理现有连接。Key 或开关已配置，只表示系统具备连接条件；供应商授权范围仍需核对。" type="warning" :closable="false" />
      <el-table :data="sources" v-loading="loading" stripe class="source-table">
        <el-table-column prop="name" label="来源" min-width="150" />
        <el-table-column label="状态" width="125"><template #default="{ row }"><el-tag :type="row.status === 'not_configured' ? 'info' : 'warning'">{{ statusLabel(row.status) }}</el-tag></template></el-table-column>
        <el-table-column v-if="canConfigure" label="登记状态" width="130"><template #default="{ row }">{{ registryLabel(authorizations.find(item => item.sourceCode === row.code)?.effectiveStatus || 'missing') }}</template></el-table-column>
        <el-table-column prop="authorization" label="授权情况" min-width="220" />
        <el-table-column prop="quota" label="额度/限速" min-width="210" />
        <el-table-column prop="retention" label="保留期" width="110" />
        <el-table-column prop="note" label="边界" min-width="230" />
      </el-table>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>模拟通道用量（UTC 日/月）</strong></template>
      <p class="hint">仅统计 P1-1C 模拟通道；现有 Quake 和站长之家请求不计入。真实供应商适配器尚未启用。</p>
      <el-table :data="sources" stripe size="small">
        <el-table-column prop="name" label="来源" min-width="190" />
        <el-table-column label="登记状态" width="140"><template #default="{ row }">{{ registryLabel(authorizations.find(item => item.sourceCode === row.code)?.effectiveStatus || 'missing') }}</template></el-table-column>
        <el-table-column label="额度占用" min-width="280"><template #default="{ row }">{{ usageText(row.code) }}</template></el-table-column>
        <el-table-column label="运行状态" min-width="170">真实请求关闭</el-table-column>
      </el-table>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>AI 准备包服务商接入状态</strong></template>
      <el-alert title="真实 AI 请求保持关闭；这里只显示接入前检查，不提供开启或填写密钥按钮。" type="warning" :closable="false" />
      <p>当前模式：{{ aiReadiness?.mode === 'simulated_only' ? '仅本地模拟' : '状态未读取' }}；外部请求：{{ aiReadiness?.performsExternalRequest ? '异常：请停止使用' : '关闭' }}；熔断状态：{{ aiReadiness?.circuitState === 'open' ? '阻断' : '未知' }}。</p>
      <p class="hint">拟允许的最小字段：{{ aiReadiness?.proposedOutboundFields.join('、') || '待核对' }}。这只是设计清单，不表示这些字段已经获供应商许可。</p>
      <ul v-if="aiReadiness"><li v-for="blocker in aiReadiness.blockers" :key="blocker">{{ blocker }}</li></ul>
      <p class="hint">真实费用、数据保留、密钥和失败审计尚未配置。当前草稿版本记录仅用于本地模拟审计，不能作为真实供应商账单。</p>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>现有连接迁移检查</strong></template>
      <el-alert title="这里仅评估 Quake 和站长之家切换条件。试算不发起供应商请求、不占用额度，显示的已用量也不含历史真实请求；现有查询仍按原路径运行。" type="warning" :closable="false" />
      <el-table :data="legacyReadiness" stripe size="small" class="source-table">
        <el-table-column label="来源" width="120"><template #default="{ row }">{{ sourceName(row.sourceCode) }}</template></el-table-column>
        <el-table-column label="现有连接" width="110"><template #default="{ row }">{{ row.existingConfigured ? '已配置' : '未配置' }}</template></el-table-column>
        <el-table-column prop="maximumPhysicalAttemptsPerAction" label="单次操作最多请求" width="150" />
        <el-table-column prop="existingAccounting" label="现有计数" min-width="250" />
        <el-table-column label="登记预检" width="115"><template #default="{ row }">{{ row.configurationPrecheckPassed ? '通过' : '未通过' }}</template></el-table-column>
        <el-table-column label="切换状态" width="110"><template #default="{ row }">{{ row.canSwitchToUnifiedMetering ? '可切换' : '暂不可切换' }}</template></el-table-column>
      </el-table>
      <div v-for="row in legacyReadiness" :key="row.sourceCode" class="blockers">
        <strong>{{ sourceName(row.sourceCode) }}：</strong>{{ row.blockers.join('；') }}
      </div>
      <div class="row">
        <el-select v-model="previewSource" aria-label="试算来源" style="width:180px" @change="onPreviewSourceChange">
          <el-option label="Quake" value="quake" /><el-option label="站长之家" value="chinaz" />
        </el-select>
        <el-input v-model="previewFields" aria-label="试算字段" placeholder="逗号分隔字段名，例如 domain,ip" style="max-width:360px" />
        <el-button :loading="previewing" @click="previewPolicy">只读试算</el-button>
        <span v-if="previewResult">结果：{{ previewLabel(previewResult.decision) }}；日 {{ previewResult.dailyUsed }}/{{ previewResult.dailyQuota ?? '未设' }}，月 {{ previewResult.monthlyUsed }}/{{ previewResult.monthlyQuota ?? '未设' }}</span>
      </div>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>现有请求观测</strong></template>
      <p class="hint">{{ observation.enabled ? '观测已开启' : '观测默认关闭' }}；已观测请求按公司保存在数据库，重启后仍可查看，不作为供应商账单或额度依据。</p>
      <el-table :data="observation.items" size="small" empty-text="暂无观测记录">
        <el-table-column prop="sourceCode" label="来源" width="130" />
        <el-table-column prop="route" label="入口" width="160" />
        <el-table-column prop="outcome" label="HTTP/结果" width="150" />
        <el-table-column prop="count" label="次数" width="100" />
      </el-table>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>额度口径模拟</strong></template>
      <p class="hint">按“每次物理请求占一次”试算，失败和 Key 回退也计入。历史观测可能不完整；此处不放行请求，也不执行限额。</p>
      <div class="row">
        <el-select v-model="simulationSource" aria-label="模拟来源" style="width:150px"><el-option label="Quake" value="quake" /><el-option label="站长之家" value="chinaz" /></el-select>
        <span>计划请求次数</span><el-input-number v-model="simulationAttempts" :min="1" :max="10" aria-label="计划请求次数" />
        <span>假设日额度</span><el-input-number v-model="simulationDailyLimit" :min="0" :max="100000000" aria-label="假设日额度" />
        <span>假设月额度</span><el-input-number v-model="simulationMonthlyLimit" :min="0" :max="100000000" aria-label="假设月额度" />
        <el-button :loading="simulating" @click="simulateQuota">模拟</el-button>
      </div>
      <p v-if="simulationResult">模拟结果：日 {{ simulationResult.observedDaily }} + {{ simulationResult.plannedAttempts }} / {{ simulationResult.dailyLimit }}（{{ simulationResult.wouldExceedDaily ? '超出' : '未超出' }}）；月 {{ simulationResult.observedMonthly }} + {{ simulationResult.plannedAttempts }} / {{ simulationResult.monthlyLimit }}（{{ simulationResult.wouldExceedMonthly ? '超出' : '未超出' }}）。历史不完整，不能据此启用真实限额。</p>
    </el-card>

    <el-card v-if="canConfigure" shadow="never">
      <template #header><strong>数据源授权资料登记</strong></template>
      <el-alert title="此处仅保存内部登记和审核记录。资料已审核不代表供应商确认许可，也不会自动开启任何新的外部请求。上表用量只统计新模拟通道，不含现有 Quake 或站长之家请求。" type="info" :closable="false" />
      <div class="row">
        <el-select v-model="selectedCode" aria-label="选择数据源" style="width:220px" @change="selectSource">
          <el-option v-for="source in sources" :key="source.code" :label="source.name" :value="source.code" />
        </el-select>
        <el-tag :type="recordStatus === 'expired' ? 'danger' : recordStatus === 'reviewed' ? 'success' : 'info'">{{ registryLabel(recordStatus) }}</el-tag>
        <span class="hint">{{ selectedRecord ? `第 ${selectedRecord.version} 版` : '暂无登记' }}</span>
      </div>
      <el-form :model="form" label-width="115px" class="registry-form">
        <el-form-item label="登记状态"><el-select v-model="form.status" :disabled="!canConfigure" style="width:180px"><el-option label="草稿" value="draft" /><el-option label="内部已审核" value="reviewed" /><el-option label="停用" value="disabled" /></el-select></el-form-item>
        <el-form-item label="授权依据"><el-input v-model="form.authorizationReference" :disabled="!canConfigure" maxlength="500" show-word-limit placeholder="仅填写协议编号或可核对的引用，不填 Key 或完整合同" /></el-form-item>
        <el-form-item label="允许字段"><el-input v-model="form.permittedFields" :disabled="!canConfigure" maxlength="1000" show-word-limit placeholder="列出许可使用的字段和用途" /></el-form-item>
        <el-form-item label="每日额度"><el-input-number v-model="form.dailyQuota" :disabled="!canConfigure" :min="0" :max="100000000" /></el-form-item>
        <el-form-item label="每月额度"><el-input-number v-model="form.monthlyQuota" :disabled="!canConfigure" :min="0" :max="100000000" /></el-form-item>
        <el-form-item label="费用说明"><el-input v-model="form.costNote" :disabled="!canConfigure" maxlength="500" show-word-limit placeholder="费用或免费依据；未知时保持草稿" /></el-form-item>
        <el-form-item label="保留天数"><el-input-number v-model="form.retentionDays" :disabled="!canConfigure" :min="0" :max="36500" /></el-form-item>
        <el-form-item label="有效期"><el-date-picker v-model="validity" :disabled="!canConfigure" type="datetimerange" start-placeholder="开始时间" end-placeholder="到期时间" /></el-form-item>
        <el-form-item v-if="canConfigure"><el-button type="primary" :loading="savingRegistry" @click="saveRegistry">保存登记</el-button></el-form-item>
      </el-form>
      <el-button link type="primary" @click="loadHistory">{{ showHistory ? '收起修改记录' : '查看修改记录' }}</el-button>
      <el-table v-if="showHistory" :data="history" stripe size="small">
        <el-table-column prop="version" label="版本" width="75" />
        <el-table-column label="时间" width="185"><template #default="{ row }">{{ new Date(row.occurredAt).toLocaleString() }}</template></el-table-column>
        <el-table-column prop="actorId" label="操作人 ID" width="110" />
        <el-table-column label="状态" width="120"><template #default="{ row }">{{ registryLabel(row.status) }}</template></el-table-column>
        <el-table-column prop="authorizationReference" label="依据" min-width="170" />
        <el-table-column prop="permittedFields" label="允许字段" min-width="180" />
        <el-table-column prop="costNote" label="费用说明" min-width="160" />
      </el-table>
    </el-card>

    <el-card v-if="canManage" shadow="never">
      <template #header><strong>Quake Key</strong></template>
      <p>状态：{{ quake.configured ? '已配置' : '未配置' }}；{{ quake.source === 'windows_credential' ? '保存在本机 Windows 凭据管理器，重启后可用' : quake.source === 'runtime' ? '仅当前进程有效' : quake.source === 'environment' ? '服务端环境配置' : '无可用 Key' }}。</p>
      <p>当前公司备用 Key：{{ quake.runtimeKeys.length }}/5。Key 不会在页面回显。</p>
      <div v-if="canConfigure" class="row">
        <el-input v-model="keyDraft" type="password" show-password autocomplete="off" aria-label="Quake Key" placeholder="新增 Quake Key" style="max-width:390px" />
        <el-button :disabled="!keyDraft.trim() || quake.runtimeKeys.length >= 5" :loading="savingKey" @click="addKey">添加 Key</el-button>
      </div>
      <div v-if="canConfigure && quake.runtimeKeys.length" class="row"><el-tag v-for="key in quake.runtimeKeys" :key="key.id" closable @close="removeKey(key.id)">Key {{ key.order }}</el-tag></div>
      <p v-if="!canConfigure" class="hint">仅有管理员角色授权权限的账号可以修改。</p>
    </el-card>

    <el-card v-if="canManage" shadow="never">
      <template #header><strong>站长之家自动查询</strong></template>
      <p>当前{{ chinaz.enabled ? '开启' : '关闭' }}；开关开启前须填写可核对的自动访问授权依据。会员资格本身不等于自动访问授权。</p>
      <div v-if="canConfigure" class="row"><el-switch v-model="chinazDraftEnabled" active-text="开启" inactive-text="关闭" /><el-input v-model="chinazReference" maxlength="500" placeholder="授权依据，例如协议编号" style="max-width:420px" /><el-button :loading="savingChinaz" @click="saveChinaz">保存</el-button></div>
      <p v-else class="hint">仅有管理员角色授权权限的账号可以修改。</p>
    </el-card>

    <el-card shadow="never">
      <template #header><strong>其他来源</strong></template>
      <p>ICP 备案：{{ sources.find(source => source.code === 'icp_manual')?.status === 'local_tool_found' ? '已找到本机 beian 程序' : '未找到本机 beian 程序' }}；仍须逐条人工触发。本页不执行查询。</p>
      <p>企业登记、百度广告迹象、招聘来源：尚无可核验授权与额度配置，自动连接保持关闭。</p>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElMessage } from 'element-plus';
import { apiDelete, apiGet, apiPost, apiPut } from '../../api/http';
import { useAuthStore } from '../../stores/auth';

interface SourceRow { code: string; name: string; status: string; authorization: string; quota: string; retention: string; note: string }
interface QuakeStatus { configured: boolean; source: string; runtimeKeys: { id: string; order: number }[] }
interface ChinazStatus { enabled: boolean; authorizationReference: string }
interface AuthorizationRecord { sourceCode: string; status: string; effectiveStatus: string; authorizationReference: string; permittedFields: string; dailyQuota: number | null; monthlyQuota: number | null; costNote: string; retentionDays: number | null; validFrom: string | null; validUntil: string | null; version: number }
interface AuthorizationRevision extends AuthorizationRecord { actorId: number; occurredAt: string }
interface SourceUsage { sourceCode: string; runtimeStatus: string; dailyQuota: number | null; monthlyQuota: number | null; dailyUsed: number; monthlyUsed: number }
interface LegacyReadiness { sourceCode: string; existingConfigured: boolean; maximumPhysicalAttemptsPerAction: number; existingAccounting: string; configurationPrecheckPassed: boolean; canSwitchToUnifiedMetering: boolean; blockers: string[] }
interface PolicyPreview { sourceCode: string; decision: string; dailyUsed: number; dailyQuota: number | null; monthlyUsed: number; monthlyQuota: number | null; performsExternalRequest: boolean }
interface RequestObservation { enabled: boolean; scope: string; billingEligible: boolean; items: { sourceCode: string; route: string; outcome: string; count: number }[] }
interface QuotaSimulation { sourceCode: string; plannedAttempts: number; observedDaily: number; observedMonthly: number; dailyLimit: number; monthlyLimit: number; wouldExceedDaily: boolean; wouldExceedMonthly: boolean; historyComplete: boolean; enforcesLimit: boolean; performsExternalRequest: boolean }
interface AiReadiness { mode: string; realProviderEnabled: boolean; performsExternalRequest: boolean; credentialInputEnabled: boolean; circuitState: string; proposedOutboundFields: string[]; blockers: string[] }
interface AuthorizationForm { status: string; authorizationReference: string; permittedFields: string; dailyQuota: number | null; monthlyQuota: number | null; costNote: string; retentionDays: number | null }
function emptyForm(): AuthorizationForm { return { status: 'draft', authorizationReference: '', permittedFields: '', dailyQuota: null, monthlyQuota: null, costNote: '', retentionDays: null }; }
const auth = useAuthStore();
const canManage = computed(() => auth.user?.permissionCodes.includes('crm.lead.manage') ?? false);
const canConfigure = computed(() => canManage.value && (auth.user?.permissionCodes.includes('identity.role.assign') ?? false));
const loading = ref(false);
const sources = ref<SourceRow[]>([]);
const authorizations = ref<AuthorizationRecord[]>([]);
const usages = ref<SourceUsage[]>([]);
const legacyReadiness = ref<LegacyReadiness[]>([]);
const observation = ref<RequestObservation>({ enabled: false, scope: 'database', billingEligible: false, items: [] });
const aiReadiness = ref<AiReadiness | null>(null);
const simulationSource = ref('quake');
const simulationAttempts = ref(2);
const simulationDailyLimit = ref(10);
const simulationMonthlyLimit = ref(100);
const simulationResult = ref<QuotaSimulation | null>(null);
const simulating = ref(false);
const previewSource = ref('quake');
const previewFields = ref('domain,ip');
const previewResult = ref<PolicyPreview | null>(null);
const previewing = ref(false);
const selectedCode = ref('quake');
const form = ref<AuthorizationForm>(emptyForm());
const validity = ref<Date[]>([]);
const history = ref<AuthorizationRevision[]>([]);
const showHistory = ref(false);
const savingRegistry = ref(false);
const selectedRecord = computed(() => authorizations.value.find(row => row.sourceCode === selectedCode.value));
const recordStatus = computed(() => selectedRecord.value?.effectiveStatus || 'missing');
const quake = ref<QuakeStatus>({ configured: false, source: 'none', runtimeKeys: [] });
const chinaz = ref<ChinazStatus>({ enabled: false, authorizationReference: '' });
const keyDraft = ref('');
const savingKey = ref(false);
const chinazDraftEnabled = ref(false);
const chinazReference = ref('');
const savingChinaz = ref(false);
function statusLabel(value: string) { return ({ connection_configured: '连接已配置', switch_enabled: '开关已开启', local_tool_found: '本机工具已找到', not_configured: '未配置' } as Record<string, string>)[value] || value; }
function registryLabel(value: string) { return ({ missing: '未登记', draft: '草稿', reviewed: '内部已审核', disabled: '已停用', expired: '已过期', not_yet_valid: '尚未生效' } as Record<string, string>)[value] || value; }
function sourceName(code: string) { return sources.value.find(row => row.code === code)?.name || code; }
function previewLabel(value: string) { return ({ registration_missing: '未完成有效登记', not_yet_valid: '尚未生效', expired: '已过期', field_denied: '字段不在登记范围', quota_exhausted: '模拟额度已用完', connection_not_configured: '现有连接未配置', policy_match_live_blocked: '登记策略匹配，真实切换仍受阻' } as Record<string, string>)[value] || value; }
function onPreviewSourceChange() { previewFields.value = previewSource.value === 'quake' ? 'domain,ip' : 'company_name,icp_number'; previewResult.value = null; }
async function previewPolicy() {
  const requestedFields = previewFields.value.split(/[,，;；\n]/).map(value => value.trim()).filter(Boolean);
  if (!requestedFields.length) { ElMessage.warning('请填写试算字段'); return; }
  previewing.value = true;
  try {
    previewResult.value = await apiPost<PolicyPreview>('/api/v1/crm/identity-candidates/sources/legacy-migration/preview', {
      sourceCode: previewSource.value, targetKind: previewSource.value === 'quake' ? 'query' : 'domain', requestedFields
    });
    if (previewResult.value?.performsExternalRequest) throw new Error('试算返回了异常的外部请求标记');
  } catch (error: any) { previewResult.value = null; ElMessage.error(error?.detail || '试算失败'); }
  finally { previewing.value = false; }
}
async function simulateQuota() {
  simulating.value = true;
  try {
    const result = await apiPost<QuotaSimulation>('/api/v1/crm/identity-candidates/sources/legacy-quota-simulation', {
      sourceCode: simulationSource.value, plannedAttempts: simulationAttempts.value,
      dailyLimit: simulationDailyLimit.value, monthlyLimit: simulationMonthlyLimit.value
    });
    if (result.enforcesLimit || result.performsExternalRequest || result.historyComplete) throw new Error('模拟结果安全标记异常');
    simulationResult.value = result;
  } catch (error: any) { simulationResult.value = null; ElMessage.error(error?.detail || '额度模拟失败'); }
  finally { simulating.value = false; }
}
function usageText(code: string) {
  const row = usages.value.find(item => item.sourceCode === code);
  if (!row) return '未登记';
  return `日 ${row.dailyUsed}/${row.dailyQuota ?? '未设'} · 月 ${row.monthlyUsed}/${row.monthlyQuota ?? '未设'}`;
}
function selectSource() {
  const row = selectedRecord.value;
  form.value = row ? { status: row.status, authorizationReference: row.authorizationReference, permittedFields: row.permittedFields, dailyQuota: row.dailyQuota, monthlyQuota: row.monthlyQuota, costNote: row.costNote, retentionDays: row.retentionDays } : emptyForm();
  validity.value = row?.validFrom && row?.validUntil ? [new Date(row.validFrom), new Date(row.validUntil)] : [];
  showHistory.value = false;
  history.value = [];
}
async function loadHistory() {
  if (showHistory.value) { showHistory.value = false; return; }
  try { const response = await apiGet<{ items: AuthorizationRevision[] }>(`/api/v1/crm/identity-candidates/sources/authorizations/${selectedCode.value}/history`); history.value = response.items; showHistory.value = true; }
  catch (error: any) { ElMessage.error(error?.detail || '修改记录读取失败'); }
}
async function saveRegistry() {
  if (!canConfigure.value) return;
  savingRegistry.value = true;
  try {
    await apiPut(`/api/v1/crm/identity-candidates/sources/authorizations/${selectedCode.value}`, {
      ...form.value, expectedVersion: selectedRecord.value?.version ?? 0,
      validFrom: validity.value?.[0]?.toISOString() || null, validUntil: validity.value?.[1]?.toISOString() || null
    });
    await refresh();
    if (showHistory.value) { showHistory.value = false; await loadHistory(); }
    ElMessage.success('授权资料已登记');
  } catch (error: any) { ElMessage.error(error?.detail || '保存失败，请刷新后重试'); }
  finally { savingRegistry.value = false; }
}
async function refresh() {
  loading.value = true;
  try {
    const [catalog, registry, usage, migration, observed, ai] = await Promise.all([
      apiGet<{ items: SourceRow[] }>('/api/v1/crm/identity-candidates/sources/status'),
      canConfigure.value ? apiGet<{ items: AuthorizationRecord[] }>('/api/v1/crm/identity-candidates/sources/authorizations') : Promise.resolve({ items: [] as AuthorizationRecord[] }),
      canConfigure.value ? apiGet<{ items: SourceUsage[] }>('/api/v1/crm/identity-candidates/sources/usage') : Promise.resolve({ items: [] as SourceUsage[] }),
      canConfigure.value ? apiGet<{ items: LegacyReadiness[] }>('/api/v1/crm/identity-candidates/sources/legacy-migration') : Promise.resolve({ items: [] as LegacyReadiness[] }),
      canConfigure.value ? apiGet<RequestObservation>('/api/v1/crm/identity-candidates/sources/legacy-request-observation') : Promise.resolve({ enabled: false, scope: 'database', billingEligible: false, items: [] }),
      canConfigure.value ? apiGet<AiReadiness>('/api/v1/crm/identity-candidates/ai-provider/readiness') : Promise.resolve(null)
    ]);
    sources.value = catalog.items;
    authorizations.value = registry.items;
    usages.value = usage.items;
    legacyReadiness.value = migration.items;
    observation.value = observed;
    aiReadiness.value = ai;
    selectSource();
    if (canManage.value) {
      const [quakeResult, chinazResult] = await Promise.all([
        apiGet<QuakeStatus>('/api/v1/crm/quake/configuration'),
        apiGet<ChinazStatus>('/api/v1/crm/identity-candidates/chinaz/status')
      ]);
      quake.value = quakeResult;
      chinaz.value = chinazResult;
      chinazDraftEnabled.value = chinazResult.enabled;
      chinazReference.value = chinazResult.authorizationReference || '';
    }
  } catch (error: any) { ElMessage.error(error?.detail || '数据源状态读取失败'); }
  finally { loading.value = false; }
}
async function addKey() {
  if (!canConfigure.value || !keyDraft.value.trim()) return;
  savingKey.value = true;
  try { await apiPut('/api/v1/crm/quake/configuration', { apiKey: keyDraft.value.trim() }); keyDraft.value = ''; await refresh(); ElMessage.success('Key 已添加'); }
  catch (error: any) { ElMessage.error(error?.detail || '添加 Key 失败'); }
  finally { keyDraft.value = ''; savingKey.value = false; }
}
async function removeKey(id: string) {
  if (!canConfigure.value) return;
  try { await apiDelete(`/api/v1/crm/quake/configuration/${id}`); await refresh(); ElMessage.success('Key 已移除'); }
  catch (error: any) { ElMessage.error(error?.detail || '移除 Key 失败'); }
}
async function saveChinaz() {
  if (!canConfigure.value) return;
  if (chinazDraftEnabled.value && !chinazReference.value.trim()) { ElMessage.warning('开启前请填写授权依据'); return; }
  savingChinaz.value = true;
  try { await apiPut('/api/v1/crm/identity-candidates/chinaz/status', { enabled: chinazDraftEnabled.value, authorizationReference: chinazReference.value.trim() }); await refresh(); ElMessage.success('设置已保存'); }
  catch (error: any) { ElMessage.error(error?.detail || '保存失败'); }
  finally { savingChinaz.value = false; }
}
onMounted(refresh);
</script>

<style scoped>
.source-settings { display: grid; gap: 16px; }
.heading, .row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.heading { justify-content: space-between; }
.source-table { margin-top: 14px; }
.row { margin-top: 12px; }
.hint { color: #909399; font-size: 12px; }
.registry-form { margin-top: 18px; max-width: 820px; }
.blockers { margin-top: 10px; color: #606266; line-height: 1.5; font-size: 12px; }
</style>
