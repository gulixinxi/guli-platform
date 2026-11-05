<template>
  <div class="crm-today-tasks">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <div style="display:flex;align-items:center;gap:12px;">
            <span class="title">今日待办</span>
            <el-radio-group v-model="filterMode" size="small" @change="loadData">
              <el-radio-button value="mine">我的</el-radio-button>
              <el-radio-button value="all" v-if="isAdmin">全部</el-radio-button>
            </el-radio-group>
          </div>
          <el-tag type="danger" v-if="overdueCount > 0">逾期 {{ overdueCount }} 条</el-tag>
        </div>
      </template>
      <el-table :data="tasks" v-loading="loading" stripe>
        <el-table-column label="状态" width="80">
          <template #default="{ row }">
            <el-tag :type="row.isOverdue ? 'danger' : 'success'" size="small">
              {{ row.isOverdue ? '已逾期' : '今日' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="companyName" label="客户名称" min-width="200" />
        <el-table-column prop="nextAction" label="下次动作" min-width="250" />
        <el-table-column prop="nextActionDate" label="计划日期" width="120" />
        <el-table-column label="操作" width="160">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="goCompany(row.companyId)">去跟进</el-button>
            <el-button v-if="can('crm.activity.manage')" link type="success" size="small" @click="handleComplete(row)">标记完成</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="tasks.length === 0 && !loading" description="今日暂无待办" />
    </el-card>
    <el-card v-if="can('crm.lead.read')" shadow="never" class="website-card">
      <template #header>
        <div class="card-header">
          <div><span class="title">网站情报待核对</span><span class="hint">当前公司范围，独立于客户跟进任务</span></div>
          <el-button size="small" :loading="websiteLoading" @click="loadWebsiteSignals">刷新</el-button>
        </div>
      </template>
      <el-alert v-if="websiteError" :title="websiteError" type="error" :closable="false" />
      <template v-if="websiteSummary">
        <p class="website-count">待处理 {{ websiteSummary.total }} 条：技术变化 {{ websiteSummary.technical }}、内容待核对 {{ websiteSummary.contentReview }}、内容差异 {{ websiteSummary.contentChange }}</p>
        <el-table :data="websiteSummary.items" size="small" stripe>
          <el-table-column prop="companyName" label="企业" min-width="170" />
          <el-table-column prop="title" label="提醒" min-width="220" />
          <el-table-column label="类型" width="125"><template #default="{ row }">{{ websiteSourceLabel(row.source) }}</template></el-table-column>
          <el-table-column label="操作" width="100"><template #default="{ row }"><el-button link type="primary" @click="openWebsiteSignal(row.id)">核对</el-button></template></el-table-column>
        </el-table>
        <el-button v-if="websiteSummary.total > websiteSummary.items.length" link type="primary" @click="router.push('/crm/signals')">查看全部提醒</el-button>
        <el-empty v-if="websiteSummary.total === 0" description="目前没有网站情报待核对" />
      </template>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import { getTodayTasks, completeActivityTask, getWebsitePendingSummary, type WebsitePendingSummary } from '../../api/crm';
import type { TodayTaskDto } from '../../types/crm';
import { useAuthStore } from '../../stores/auth';
import { useCrmPermission } from '../../composables/useCrmPermission';

const router = useRouter();
const auth = useAuthStore();
const can = useCrmPermission();
const loading = ref(false);
const tasks = ref<TodayTaskDto[]>([]);
const websiteSummary = ref<WebsitePendingSummary | null>(null);
const websiteLoading = ref(false);
const websiteError = ref('');
const filterMode = ref<'mine' | 'all'>('mine');

const isAdmin = computed(() => auth.user?.isPlatformAdmin || auth.user?.userName === 'admin');
const currentUserId = computed(() => auth.user?.userId ? Number(auth.user.userId) : null);

const overdueCount = computed(() => tasks.value.filter(t => t.isOverdue).length);

async function loadData() {
  loading.value = true;
  try {
    const ownerId = filterMode.value === 'mine' && currentUserId.value ? currentUserId.value : undefined;
    tasks.value = await getTodayTasks(ownerId);
  } catch (e: any) {
    ElMessage.error(e.message || '加载失败');
  } finally {
    loading.value = false;
  }
}

function goCompany(id: string) {
  router.push(`/crm/companies/${id}`);
}

function websiteSourceLabel(source: string) {
  return ({ 'website-diagnostic': '技术变化', 'website-content-review': '内容待核对', 'website-content-change': '内容差异' } as Record<string, string>)[source] || source;
}
function openWebsiteSignal(id: string) { router.push({ path: '/crm/signals', query: { websiteSignalId: id } }); }
async function loadWebsiteSignals() {
  if (!can('crm.lead.read')) return;
  websiteLoading.value = true; websiteError.value = ''; websiteSummary.value = null;
  try { websiteSummary.value = await getWebsitePendingSummary(); }
  catch (error: any) { websiteError.value = error?.detail || '网站提醒加载失败'; }
  finally { websiteLoading.value = false; }
}

async function handleComplete(row: TodayTaskDto) {
  try {
    await completeActivityTask(row.activityId);
    ElMessage.success('已标记完成');
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '操作失败');
  }
}

onMounted(() => { loadData(); loadWebsiteSignals(); });
</script>

<style scoped>
.crm-today-tasks { padding: 16px; }
.card-header { display: flex; justify-content: space-between; align-items: center; }
.title { font-size: 16px; font-weight: 600; }
.website-card { margin-top: 16px; }
.hint { margin-left: 10px; color: #909399; font-size: 12px; }
.website-count { margin: 0 0 12px; color: #606266; }
</style>
