<template>
  <div class="dashboard">
    <h2 class="page-title">经营看板</h2>

    <el-alert
      v-if="can('identity.role.assign') && backupStatus?.overall !== 'PASS'"
      :title="backupStatus?.overall === 'FAIL' ? '正式库备份异常，请管理员立即检查' : '正式库备份状态未知，请管理员检查监控任务'"
      type="error"
      :closable="false"
      show-icon
      style="margin-bottom:16px;"
    />
    <el-alert
      v-if="can('identity.role.assign') && backupStatus?.overall === 'PASS'"
      :title="`正式库备份正常；最近检查：${backupStatus.checkedAtUtc ? new Date(backupStatus.checkedAtUtc).toLocaleString() : '未知'}`"
      type="success"
      :closable="false"
      show-icon
      style="margin-bottom:16px;"
    />

    <el-alert v-if="loadError" title="看板数据加载失败，请重试。" type="error" :closable="false" show-icon>
      <template #default>
        <el-button link type="primary" @click="loadStats">重新加载</el-button>
      </template>
    </el-alert>
    <el-skeleton v-if="loading" :rows="8" animated />
    <template v-else-if="stats">
    <!-- 逾期跟进提醒横幅 -->
    <el-alert
      v-if="stats?.overdue.count && stats.overdue.count > 0"
      :title="`有 ${stats.overdue.count} 条跟进任务已逾期，请及时处理`"
      type="error"
      :closable="false"
      show-icon
      style="margin-bottom:16px;"
    >
      <template #default>
        <div style="margin-top:8px;">
          <div v-for="item in stats.overdue.items" :key="item.activityId" style="margin-bottom:4px;font-size:13px;">
            <span style="color:#f56c6c;font-weight:600;">[逾期 {{ item.nextActionDate }}]</span>
            <span>{{ item.companyName }}</span>
            <span style="color:#909399;">→ {{ item.nextAction }}</span>
            <el-button link type="primary" size="small" @click="goCompany(item.companyId)">去跟进</el-button>
          </div>
          <el-button link type="primary" size="small" @click="$router.push('/crm/tasks')">查看全部待办 →</el-button>
        </div>
      </template>
    </el-alert>

    <!-- 今日待办提醒（无逾期但有今日任务） -->
    <el-alert
      v-else-if="stats?.overdue.todayCount && stats.overdue.todayCount > 0"
      :title="`今日有 ${stats.overdue.todayCount} 条跟进任务待处理`"
      type="warning"
      :closable="false"
      show-icon
      style="margin-bottom:16px;"
    >
      <template #default>
        <el-button link type="primary" size="small" @click="$router.push('/crm/tasks')">查看今日待办 →</el-button>
      </template>
    </el-alert>

    <!-- 顶部统计卡片 -->
    <el-card v-if="can('crm.lead.read')" shadow="never" class="website-reminders">
      <template #header><div class="website-header"><strong>网站情报待核对</strong><el-button link type="primary" :loading="websiteLoading" @click="loadWebsiteSignals">刷新</el-button></div></template>
      <el-alert v-if="websiteError" :title="websiteError" type="error" :closable="false" />
      <template v-else-if="websiteSummary">
        <span>待处理 {{ websiteSummary.total }} 条：技术变化 {{ websiteSummary.technical }}、内容待核对 {{ websiteSummary.contentReview }}、内容差异 {{ websiteSummary.contentChange }}</span>
        <el-button link type="primary" @click="router.push('/crm/signals')">到情报信号处理</el-button>
      </template>
    </el-card>

    <el-row :gutter="16" class="stat-cards">
      <el-col :span="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">线索总数</div>
          <div class="stat-value">{{ stats?.funnel.leads ?? '-' }}</div>
        </el-card>
      </el-col>
      <el-col :span="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">客户主档</div>
          <div class="stat-value">{{ stats?.funnel.companies ?? '-' }}</div>
        </el-card>
      </el-col>
      <el-col :span="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">进行中商机</div>
          <div class="stat-value" style="color:#409eff">{{ stats?.funnel.activeDeals ?? '-' }}</div>
        </el-card>
      </el-col>
      <el-col :span="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-label">已成交</div>
          <div class="stat-value" style="color:#67c23a">{{ stats?.funnel.wonDeals ?? '-' }}</div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 转化率 + 金额 -->
    <el-row :gutter="16" class="stat-cards">
      <el-col :span="12">
        <el-card shadow="hover">
          <template #header><span>转化率</span></template>
          <div class="conv-row">
            <span>线索 → 客户</span>
            <el-progress :percentage="stats?.conversion.leadToCompany ?? 0" :stroke-width="14" />
          </div>
          <div class="conv-row">
            <span>客户 → 商机</span>
            <el-progress :percentage="stats?.conversion.companyToDeal ?? 0" :stroke-width="14" />
          </div>
          <div class="conv-row">
            <span>商机 → 成交</span>
            <el-progress :percentage="stats?.conversion.dealToWin ?? 0" :stroke-width="14" status="success" />
          </div>
        </el-card>
      </el-col>
      <el-col :span="12">
        <el-card shadow="hover">
          <template #header><span>商机金额（元）</span></template>
          <div class="amount-row">
            <span>进行中预计</span>
            <span class="amount-value" style="color:#409eff">¥{{ formatAmount(stats?.amount.active) }}</span>
          </div>
          <div class="amount-row">
            <span>已成交</span>
            <span class="amount-value" style="color:#67c23a">¥{{ formatAmount(stats?.amount.won) }}</span>
          </div>
          <div class="amount-row">
            <span>已丢单</span>
            <span class="amount-value" style="color:#909399">¥{{ formatAmount(stats?.amount.lost) }}</span>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <!-- 销售漏斗 -->
    <el-card shadow="hover" class="funnel-card">
      <template #header><span>销售漏斗</span></template>
      <div class="funnel">
        <div class="funnel-stage" v-for="s in funnelStages" :key="s.label">
          <div class="funnel-label">{{ s.label }}</div>
          <div class="funnel-bar-wrap">
            <div class="funnel-bar" :style="{ width: s.width + '%', background: s.color }">
              <span class="funnel-count">{{ s.count }}</span>
            </div>
          </div>
        </div>
      </div>
    </el-card>

    <!-- 按阶段 + 按负责人 -->
    <el-row :gutter="16">
      <el-col :span="12">
        <el-card shadow="hover">
          <template #header><span>商机阶段分布</span></template>
          <el-table :data="stats?.byStage ?? []" size="small">
            <el-table-column prop="label" label="阶段" />
            <el-table-column prop="count" label="商机数" width="100" />
            <el-table-column label="占比">
              <template #default="{ row }">
                {{ stagePercent(row.count) }}%
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
      <el-col :span="12">
        <el-card shadow="hover">
          <template #header><span>按负责人（进行中商机）</span></template>
          <el-table :data="stats?.byOwner ?? []" size="small">
            <el-table-column prop="ownerId" label="负责人ID" width="100" />
            <el-table-column prop="dealCount" label="商机数" width="100" />
            <el-table-column label="预计金额">
              <template #default="{ row }">¥{{ formatAmount(row.amount) }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useRouter } from 'vue-router';
import { getBackupMonitorStatus, getDashboardStats, getWebsitePendingSummary, type BackupMonitorStatus, type DashboardStats, type WebsitePendingSummary } from '../../api/crm';
import { useCrmPermission } from '../../composables/useCrmPermission';

const router = useRouter();
const can = useCrmPermission();
const stats = ref<DashboardStats | null>(null);
const websiteSummary = ref<WebsitePendingSummary | null>(null);
const websiteLoading = ref(false);
const websiteError = ref('');
const backupStatus = ref<BackupMonitorStatus | null>(null);
let backupStatusTimer: ReturnType<typeof setInterval> | undefined;
const loading = ref(false);
const loadError = ref(false);

function goCompany(id: number) {
  router.push(`/crm/companies/${id}`);
}

async function loadStats() {
  loading.value = true;
  loadError.value = false;
  stats.value = null;
  try {
    stats.value = await getDashboardStats();
  } catch {
    loadError.value = true;
  } finally {
    loading.value = false;
  }
}

async function loadWebsiteSignals() {
  if (!can('crm.lead.read')) return;
  websiteLoading.value = true; websiteError.value = ''; websiteSummary.value = null;
  try { websiteSummary.value = await getWebsitePendingSummary(); }
  catch (error: any) { websiteError.value = error?.detail || '网站提醒加载失败'; }
  finally { websiteLoading.value = false; }
}

async function loadBackupStatus() {
  try { backupStatus.value = await getBackupMonitorStatus(); }
  catch { backupStatus.value = null; }
}

onMounted(() => {
  loadStats(); loadWebsiteSignals();
  if (can('identity.role.assign')) {
    loadBackupStatus();
    backupStatusTimer = setInterval(loadBackupStatus, 60_000);
  }
});

onUnmounted(() => {
  if (backupStatusTimer) clearInterval(backupStatusTimer);
});

const funnelStages = computed(() => {
  if (!stats.value) return [];
  const f = stats.value.funnel;
  const max = Math.max(f.leads, 1);
  return [
    { label: '线索', count: f.leads, width: 100, color: '#909399' },
    { label: '客户', count: f.companies, width: (f.companies / max) * 100, color: '#409eff' },
    { label: '进行中商机', count: f.activeDeals, width: (f.activeDeals / max) * 100, color: '#e6a23c' },
    { label: '成交', count: f.wonDeals, width: (f.wonDeals / max) * 100, color: '#67c23a' },
  ];
});

function stagePercent(count: number): string {
  const total = stats.value?.funnel.activeDeals ?? 0;
  if (total === 0) return '0';
  return ((count / total) * 100).toFixed(0);
}

function formatAmount(v?: number): string {
  if (v == null) return '0';
  return v.toLocaleString('zh-CN', { maximumFractionDigits: 0 });
}
</script>

<style scoped>
.dashboard { padding: 16px; }
.page-title { margin: 0 0 16px; font-size: 20px; }
.website-reminders { margin-bottom: 16px; }
.website-header { display: flex; justify-content: space-between; align-items: center; }
.stat-cards { margin-bottom: 16px; }
.stat-card { text-align: center; }
.stat-label { color: #909399; font-size: 14px; margin-bottom: 8px; }
.stat-value { font-size: 28px; font-weight: 600; }
.conv-row { display: flex; align-items: center; gap: 12px; margin-bottom: 12px; }
.conv-row span { width: 90px; font-size: 13px; color: #606266; }
.conv-row .el-progress { flex: 1; }
.amount-row { display: flex; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid #f0f0f0; }
.amount-row:last-child { border-bottom: none; }
.amount-value { font-weight: 600; font-size: 16px; }
.funnel-card { margin-bottom: 16px; }
.funnel-stage { display: flex; align-items: center; margin-bottom: 10px; }
.funnel-label { width: 100px; font-size: 13px; color: #606266; }
.funnel-bar-wrap { flex: 1; background: #f5f7fa; border-radius: 4px; height: 28px; }
.funnel-bar { height: 100%; border-radius: 4px; display: flex; align-items: center; justify-content: flex-end; padding-right: 8px; min-width: 40px; transition: width 0.3s; }
.funnel-count { color: #fff; font-size: 13px; font-weight: 600; }
</style>
