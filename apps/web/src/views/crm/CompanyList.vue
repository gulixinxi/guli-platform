<template>
  <div class="crm-company-list">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span class="title">客户主档</span>
          <div>
          <el-button v-if="can('crm.company.export')" :disabled="loading || !!loadError || list.length === 0" @click="handleExport" :icon="Download">导出当前页</el-button>
            <el-button v-if="can('crm.company.manage')" type="primary" @click="showCreate = true" :icon="Plus">新建客户</el-button>
          </div>
        </div>
      </template>

      <el-form :inline="true" :model="filters" class="search-form">
        <el-form-item label="关键词">
          <el-input v-model="filters.keyword" placeholder="公司名/信用代码" clearable @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="filters.status" placeholder="全部" clearable>
            <el-option label="待联系" :value="0" />
            <el-option label="跟进中" :value="1" />
            <el-option label="已报价" :value="2" />
            <el-option label="成交" :value="3" />
            <el-option label="暂搁" :value="4" />
            <el-option label="无效" :value="5" />
          </el-select>
        </el-form-item>
        <el-form-item label="评级">
          <el-select v-model="filters.rating" placeholder="全部" clearable>
            <el-option label="A" :value="0" />
            <el-option label="B" :value="1" />
            <el-option label="C" :value="2" />
            <el-option label="D" :value="3" />
          </el-select>
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="search" :icon="Search">搜索</el-button>
          <el-button @click="resetFilters" :icon="Refresh">重置</el-button>
        </el-form-item>
      </el-form>

      <el-alert v-if="loadError" type="error" :closable="false" class="load-alert" :title="loadError">
        <el-button link type="primary" @click="loadData">重新加载</el-button>
      </el-alert>
      <el-table v-if="!loadError" :data="list" v-loading="loading" stripe @row-click="goDetail">
        <template #empty>没有符合条件的客户</template>
        <el-table-column prop="companyNo" label="客户编号" width="140" />
        <el-table-column prop="fullName" label="公司全称" min-width="200" />
        <el-table-column prop="creditCode" label="信用代码" width="160" show-overflow-tooltip />
        <el-table-column label="城市" width="80">
          <template #default="{ row }">{{ cityLabel(row.city) }}</template>
        </el-table-column>
        <el-table-column label="行业" width="100">
          <template #default="{ row }">{{ industryLabel(row.industry) }}</template>
        </el-table-column>
        <el-table-column label="评级" width="70">
          <template #default="{ row }">
            <el-tag v-if="row.rating !== undefined" :type="ratingTagType(row.rating)">{{ ratingLabel(row.rating) }}</el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="statusTagType(row.status)">{{ statusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="标签" min-width="150">
          <template #default="{ row }">
            <el-tag v-for="tag in (row.tags || [])" :key="tag" size="small" effect="plain" style="margin:2px">
              {{ tag }}
            </el-tag>
            <el-button v-if="can('crm.company.manage')" link type="primary" size="small" @click.stop="handleEditTags(row)">编辑</el-button>
          </template>
        </el-table-column>
        <el-table-column prop="createdAt" label="创建时间" width="160">
          <template #default="{ row }">{{ formatDate(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="120" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click.stop="goDetail(row)">详情</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-if="!loadError" class="pagination">
        <el-pagination
          v-model:current-page="filters.page"
          v-model:page-size="filters.pageSize"
          :total="total"
          :page-sizes="[10, 20, 50]"
          layout="total, sizes, prev, pager, next"
          @size-change="onPageSizeChange"
          @current-change="loadData"
        />
      </div>
    </el-card>

    <el-dialog v-model="showCreate" title="新建客户" width="600px">
      <el-form :model="form" label-width="120px">
        <el-form-item label="公司全称" required>
          <el-input v-model="form.fullName" />
        </el-form-item>
        <el-form-item label="统一社会信用代码">
          <el-input v-model="form.creditCode" maxlength="18" />
        </el-form-item>
        <el-form-item label="所在城市" required>
          <el-select v-model="form.city">
            <el-option label="潍坊" :value="0" />
            <el-option label="青岛" :value="1" />
            <el-option label="烟台" :value="2" />
            <el-option label="济南" :value="3" />
            <el-option label="其他" :value="99" />
          </el-select>
        </el-form-item>
        <el-form-item label="所属行业" required>
          <el-select v-model="form.industry">
            <el-option label="机械制造" :value="0" />
            <el-option label="纺织服装" :value="1" />
            <el-option label="食品加工" :value="2" />
            <el-option label="化工" :value="3" />
            <el-option label="建材" :value="4" />
            <el-option label="农业种植" :value="5" />
            <el-option label="商贸流通" :value="6" />
            <el-option label="服务业" :value="7" />
            <el-option label="其他" :value="99" />
          </el-select>
        </el-form-item>
        <el-form-item label="是否有官网">
          <el-switch v-model="form.hasWebsite" />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="form.remarks" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate = false">取消</el-button>
        <el-button type="primary" @click="handleCreate" :loading="creating">创建</el-button>
      </template>
    </el-dialog>

    <!-- 标签编辑对话框 -->
    <el-dialog v-model="showTagsDialog" title="编辑标签" width="400px">
      <div style="margin-bottom:12px">
        <el-input v-model="tagsInput" placeholder="输入标签后按回车添加" @keyup.enter="addTag">
          <template #append><el-button @click="addTag">添加</el-button></template>
        </el-input>
      </div>
      <div style="min-height:60px">
        <el-tag v-for="tag in tagsList" :key="tag" closable style="margin:4px" @close="removeTag(tag)">{{ tag }}</el-tag>
        <span v-if="tagsList.length === 0" style="color:#909399;font-size:13px">暂无标签</span>
      </div>
      <div style="margin-top:12px;font-size:12px;color:#909399">
        常用：
        <el-link v-for="t in ['百度推广','重点客户','已报价','待回访','高意向','老客户']" :key="t" type="primary" style="margin-right:8px" @click="() => { if(!tagsList.includes(t)) tagsList.push(t); }">{{t}}</el-link>
      </div>
      <template #footer>
        <el-button @click="showTagsDialog = false">取消</el-button>
        <el-button type="primary" @click="saveTags">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { ElMessage } from 'element-plus';
import { Plus, Search, Refresh, Download } from '@element-plus/icons-vue';
import { listCompanies, createCompany, updateCompanyTags } from '../../api/crm';
import type { CompanyDto, CreateCompanyRequest } from '../../types/crm';
import { CustomerStatus, IntentRating, CompanyCity, CompanyIndustry } from '../../types/crm';
import { crmEnumNumber, crmLabel, customerStatusLabels, intentRatingLabels, companyCityLabels, companyIndustryLabels } from '../../utils/crmEnum';
import { exportToExcel } from '../../utils/exportExcel';
import { useCrmPermission } from '../../composables/useCrmPermission';

const router = useRouter();
const can = useCrmPermission();
const loading = ref(false);
const loadError = ref('');
let latestRequest = 0;
const creating = ref(false);
const list = ref<CompanyDto[]>([]);
const total = ref(0);
const showCreate = ref(false);
const showTagsDialog = ref(false);
const tagsEditCompany = ref<CompanyDto | null>(null);
const tagsInput = ref('');
const tagsList = ref<string[]>([]);

const filters = reactive({
  keyword: '',
  status: undefined as number | undefined,
  rating: undefined as number | undefined,
  page: 1,
  pageSize: 20
});

const form = reactive<CreateCompanyRequest>({
  fullName: '',
  creditCode: '',
  city: 0,
  industry: 0,
  hasWebsite: false,
  source: 99,
  remarks: ''
});

async function loadData() {
  const request = ++latestRequest;
  loading.value = true;
  loadError.value = '';
  try {
    const result = await listCompanies({ ...filters });
    if (request !== latestRequest) return;
    list.value = result.items;
    total.value = result.total;
  } catch (e: any) {
    if (request !== latestRequest) return;
    list.value = [];
    total.value = 0;
    loadError.value = e?.status === 403 ? '没有当前公司客户的读取权限。' : '客户列表加载失败，请重新加载。';
  } finally {
    if (request === latestRequest) loading.value = false;
  }
}

function search() {
  filters.page = 1;
  loadData();
}

function onPageSizeChange() {
  filters.page = 1;
  loadData();
}

function resetFilters() {
  filters.keyword = '';
  filters.status = undefined;
  filters.rating = undefined;
  filters.page = 1;
  loadData();
}

function goDetail(row: CompanyDto) {
  router.push(`/crm/companies/${row.id}`);
}

async function handleCreate() {
  if (!form.fullName.trim()) {
    ElMessage.warning('请输入公司全称');
    return;
  }
  creating.value = true;
  try {
    await createCompany(form);
    ElMessage.success('创建成功');
    showCreate.value = false;
    Object.assign(form, { fullName: '', creditCode: '', city: 0, industry: 0, hasWebsite: false, remarks: '' });
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '创建失败');
  } finally {
    creating.value = false;
  }
}

function handleEditTags(row: CompanyDto) {
  tagsEditCompany.value = row;
  tagsList.value = [...(row.tags || [])];
  tagsInput.value = '';
  showTagsDialog.value = true;
}

function addTag() {
  const t = tagsInput.value.trim();
  if (t && !tagsList.value.includes(t)) tagsList.value.push(t);
  tagsInput.value = '';
}

function removeTag(tag: string) {
  tagsList.value = tagsList.value.filter(t => t !== tag);
}

async function saveTags() {
  if (!tagsEditCompany.value) return;
  try {
    await updateCompanyTags(tagsEditCompany.value.id, tagsList.value);
    tagsEditCompany.value.tags = [...tagsList.value];
    ElMessage.success('标签已更新');
    showTagsDialog.value = false;
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || '更新失败');
  }
}

function statusLabel(s: number) { return crmLabel(customerStatusLabels, s, CustomerStatus); }
function statusTagType(s: number) { return ['info', 'warning', 'primary', 'success', 'info', 'danger'][crmEnumNumber(s, CustomerStatus)] || 'info'; }
function ratingLabel(r: number) { return crmLabel(intentRatingLabels, r, IntentRating, '-'); }
function ratingTagType(r: number) { return ['success', 'warning', 'info', 'danger'][crmEnumNumber(r, IntentRating)] || 'info'; }
function cityLabel(c: number) { return crmLabel(companyCityLabels, c, CompanyCity, '其他'); }
function industryLabel(i: number) { return crmLabel(companyIndustryLabels, i, CompanyIndustry, '其他'); }
function formatDate(d: string) { return new Date(d).toLocaleString('zh-CN'); }

onMounted(loadData);

function handleExport() {
  if (list.value.length === 0) { ElMessage.warning('没有可导出的数据'); return; }
  exportToExcel(list.value, [
    { key: 'fullName', label: '公司全称' },
    { key: 'shortName', label: '简称' },
    { key: 'creditCode', label: '统一社会信用代码' },
    { key: 'industry', label: '行业' },
    { key: 'customerStatus', label: '客户状态' },
    { key: 'websiteDomain', label: '官网' },
    { key: 'address', label: '地址' },
    { key: 'contactName', label: '主联系人' },
    { key: 'mobile', label: '手机' },
    { key: 'email', label: '邮箱' },
    { key: 'createdAt', label: '创建时间' },
  ], '客户主档导出');
  ElMessage.success(`已导出当前页 ${list.value.length} 个客户`);
}
</script>

<style scoped>
.crm-company-list { padding: 16px; }
.card-header { display: flex; justify-content: space-between; align-items: center; }
.title { font-size: 16px; font-weight: 600; }
.search-form { margin-bottom: 16px; }
.load-alert { margin-bottom: 16px; }
.pagination { margin-top: 16px; display: flex; justify-content: flex-end; }
</style>
