<template>
  <div class="crm-lead-list">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span class="title">线索池</span>
          <div>
            <el-button v-if="can('crm.lead.export')" @click="handleExport" :icon="Download">导出Excel</el-button>
            <el-button v-if="can('crm.lead.import')" @click="showImport = true" :icon="Upload">批量导入</el-button>
            <el-button v-if="can('crm.lead.manage')" type="primary" @click="showCreate = true" :icon="Plus">新建线索</el-button>
          </div>
        </div>
      </template>

      <el-form :inline="true" :model="filters" class="search-form">
        <el-form-item label="关键词">
          <el-input v-model="filters.keyword" placeholder="公司名/联系人/手机" clearable @keyup.enter="loadData" />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="filters.status" placeholder="全部" clearable>
            <el-option label="待认领" :value="0" />
            <el-option label="已认领" :value="1" />
            <el-option label="已转化" :value="2" />
            <el-option label="已废弃" :value="3" />
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
        <el-form-item label="负责人">
          <el-select v-model="filters.ownerId" placeholder="全部" clearable>
            <el-option v-for="m in teamMembers" :key="m.id" :label="m.displayName || m.userName" :value="m.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="主体类型">
          <el-select v-model="filters.subjectType" placeholder="全部" clearable>
            <el-option label="企业" :value="0" />
            <el-option label="协会/商会" :value="1" />
            <el-option label="服务中心" :value="2" />
            <el-option label="专业服务(会计/律师/咨询)" :value="3" />
            <el-option label="园区/基地" :value="4" />
            <el-option label="政府机关" :value="5" />
            <el-option label="事业单位" :value="6" />
            <el-option label="学历教育(学校/幼儿园/大学)" :value="7" />
            <el-option label="教育培训(托管/教培/督导)" :value="8" />
            <el-option label="个体商户/门店(餐饮/零售/服务)" :value="10" />
          </el-select>
        </el-form-item>
        <el-form-item label="标签">
          <el-input v-model="filters.tag" placeholder="按标签筛选" clearable style="width:140px" @keyup.enter="loadData" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="loadData" :icon="Search">搜索</el-button>
          <el-button @click="resetFilters" :icon="Refresh">重置</el-button>
          <el-button v-if="currentUserId" :type="filters.ownerId === currentUserId ? 'warning' : 'default'" @click="filterMine">我的线索</el-button>
        </el-form-item>
      </el-form>

      <el-table :data="list" v-loading="loading" stripe>
        <el-table-column prop="companyName" label="公司名称" min-width="180" />
        <el-table-column label="主体类型" width="130">
          <template #default="{ row }">
            <el-tag :type="subjectTypeTagType(row.subjectType)" size="small">{{ subjectTypeLabel(row.subjectType) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="contactName" label="联系人" width="100" />
        <el-table-column prop="mobile" label="手机" width="120" />
        <el-table-column prop="websiteDomain" label="官网" width="150" show-overflow-tooltip />
        <el-table-column label="AI评分" width="80">
          <template #default="{ row }">
            <el-tag :type="scoreTagType(row.aiScore)">{{ row.aiScore ?? '-' }}</el-tag>
          </template>
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
        <el-table-column label="负责人" width="100">
          <template #default="{ row }">
            <span>{{ ownerName(row.ownerId) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="标签" min-width="150">
          <template #default="{ row }">
            <el-tag v-for="tag in (row.tags || [])" :key="tag" size="small" effect="plain" style="margin:2px">
              {{ tag }}
            </el-tag>
            <el-button v-if="can('crm.lead.manage')" link type="primary" size="small" @click="handleEditTags(row)">编辑</el-button>
          </template>
        </el-table-column>
        <el-table-column prop="createdAt" label="创建时间" width="160">
          <template #default="{ row }">{{ formatDate(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="260" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openAssessment(row)">需求评估</el-button>
            <el-button link type="primary" size="small" @click="handleClaim(row)" v-if="can('crm.lead.claim') && crmEnumNumber(row.status, LeadStatus) === LeadStatus.Pending">认领</el-button>
            <el-button v-if="can('crm.lead.manage')" link type="primary" size="small" @click="handleAssign(row)">分配</el-button>
            <el-button link type="primary" size="small" @click="handleConvert(row)" v-if="can('crm.lead.convert') && crmEnumNumber(row.status, LeadStatus) !== LeadStatus.Converted && crmEnumNumber(row.status, LeadStatus) !== LeadStatus.Discarded">转化</el-button>
            <el-button link type="warning" size="small" @click="handleDiscard(row)" v-if="can('crm.lead.manage') && crmEnumNumber(row.status, LeadStatus) !== LeadStatus.Discarded">废弃</el-button>
            <el-button v-if="can('crm.lead.manage')" link type="danger" size="small" @click="handleDelete(row)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination">
        <el-pagination
          v-model:current-page="filters.page"
          v-model:page-size="filters.pageSize"
          :total="total"
          :page-sizes="[10, 20, 50]"
          layout="total, sizes, prev, pager, next"
          @size-change="loadData"
          @current-change="loadData"
        />
      </div>
    </el-card>

    <el-dialog v-model="showAssessment" :title="`需求初评 · ${assessmentLead?.companyName || ''}`" width="760px">
      <el-alert type="info" :closable="false" title="四类方向分别判断。没有证据时保持未知；可能或已确认需求必须填写证据，保存不会自动创建商机。" />
      <p v-if="assessmentOriginId" class="assessment-origin">关联单位反查记录 #{{ assessmentOriginId }}</p>
      <div v-for="item in assessmentRows" :key="item.category" class="assessment-row">
        <div class="assessment-row-header">
          <strong>{{ assessmentCategoryLabel(item.category) }}</strong>
          <span>版本 {{ item.version }}{{ item.reviewedAt ? ` · ${new Date(item.reviewedAt).toLocaleString()}` : '' }}</span>
        </div>
        <el-select v-model="item.state" placeholder="评估状态" style="width:180px">
          <el-option label="未知" value="unknown" /><el-option label="待核验" value="pending" />
          <el-option label="可能有需求" value="potential" /><el-option label="已确认需求" value="confirmed" />
          <el-option label="不适用" value="not_applicable" />
        </el-select>
        <el-select v-model="item.confidence" clearable placeholder="置信度" style="width:130px;margin-left:8px">
          <el-option label="低" value="low" /><el-option label="中" value="medium" /><el-option label="高" value="high" />
        </el-select>
        <div class="evidence-block" style="margin-top:8px">
          <div class="evidence-head">
            <span>证据</span>
            <el-button v-if="can('crm.lead.manage')" size="small" link type="primary" @click="addEvidenceItem(item)">+ 添加证据</el-button>
          </div>
          <div v-for="(ev, i) in item.evidenceItems" :key="i" class="evidence-row">
            <el-select v-model="ev.type" :disabled="!can('crm.lead.manage')" placeholder="类型" style="width:130px">
              <el-option v-for="o in evidenceTypeOptions" :key="o.value" :label="o.label" :value="o.value" />
            </el-select>
            <el-select v-model="ev.verificationStatus" :disabled="!can('crm.lead.manage')" placeholder="核验状态" style="width:150px">
              <el-option v-for="o in evidenceVerificationOptions" :key="o.value" :label="o.label" :value="o.value" />
            </el-select>
            <el-input v-model="ev.url" :disabled="!can('crm.lead.manage')" placeholder="来源链接（可选）" style="flex:1;min-width:180px" />
            <el-input v-model="ev.note" :disabled="!can('crm.lead.manage')" type="textarea" :rows="1" maxlength="2000" placeholder="证据说明" style="flex:1;min-width:180px" />
            <el-button v-if="can('crm.lead.manage')" size="small" link type="danger" @click="item.evidenceItems.splice(i, 1)">删除</el-button>
          </div>
          <div v-if="!item.evidenceItems?.length" class="evidence-empty">暂无证据。可能/已确认需求至少需要一条。</div>
        </div>
        <el-input v-model="item.reason" maxlength="1000" placeholder="判断理由" style="margin-top:8px" />
        <el-input v-model="item.nextAction" maxlength="500" placeholder="下一步核验或联系动作" style="margin-top:8px" />
        <el-button v-if="can('crm.lead.manage')" type="primary" size="small" :loading="savingAssessment === item.category" style="margin-top:8px" @click="saveAssessment(item)">保存本项</el-button>
        <el-button v-if="item.state === 'confirmed' && item.id && can('crm.deal.manage')" size="small" type="success" style="margin-top:8px" @click="openDealFromAssessment(item)">创建商机</el-button>
        <el-button v-if="item.category === 'website' || item.category === 'geo'" size="small" style="margin-top:8px" @click="openVisitHandoff(item)">拜访/项目交接预检</el-button>
        <el-button size="small" style="margin-top:8px" @click="toggleAssessmentHistory(item.category)">{{ expandedHistoryCategory === item.category ? '收起历史' : '查看历史' }}</el-button>
        <el-table v-if="expandedHistoryCategory === item.category" :data="assessmentHistory[item.category] || []" size="small" stripe style="margin-top:8px">
          <el-table-column prop="version" label="版本" width="65" />
          <el-table-column prop="state" label="状态" width="100"><template #default="{ row }">{{ assessmentStateLabel(row.state) }}</template></el-table-column>
          <el-table-column prop="evidence" label="证据(旧)" width="140" show-overflow-tooltip />
          <el-table-column prop="reason" label="理由" min-width="130" show-overflow-tooltip />
          <el-table-column prop="reviewerId" label="审核人" width="95"><template #default="{ row }">#{{ row.reviewerId }}</template></el-table-column>
          <el-table-column prop="reviewedAt" label="时间" width="160"><template #default="{ row }">{{ new Date(row.reviewedAt).toLocaleString() }}</template></el-table-column>
        </el-table>
      </div>
    </el-dialog>

    <el-dialog v-model="showVisitHandoff" title="拜访与建站/GEO 交接预检" width="620px" append-to-body>
      <el-alert type="info" :closable="false" title="只读预检：不上传拜访文件、不创建信号、商机或工单，也不连接 NAS/ERP。" />
      <p>{{ assessmentLead?.companyName }} · {{ assessmentCategoryLabel(handoffCategory) }}</p>
      <div class="handoff-controls">
        <el-select v-model="handoffKind" style="width:160px"><el-option label="拜访外部引用" value="visit" /><el-option label="网站报告 ID" value="website_report" /></el-select>
        <el-input v-model="handoffReference" :placeholder="handoffKind === 'visit' ? '仅填外部编号，不填路径或文件内容' : '填写已完成报告 ID'" maxlength="100" style="flex:1" />
        <el-button :loading="handoffLoading" :disabled="!handoffReference.trim()" @click="previewVisitHandoff">预检</el-button>
      </div>
      <el-alert v-if="handoffPreview" type="warning" :closable="false" :title="`交接状态：${handoffPreview.handoffStatus === 'pending_sync' ? '待同步' : handoffPreview.handoffStatus}`" />
      <p v-if="handoffPreview" class="assessment-origin">来源核验：{{ handoffPreview.sourceVerification === 'confirmed_internal_report' ? '库内报告已核对归属' : '外部拜访引用未核验' }}；需求评估：{{ handoffPreview.assessmentConfirmed ? '已确认' : '未确认' }}；现有商机：{{ handoffPreview.existingDeal ? '有' : '无' }}。</p>
      <ul v-if="handoffPreview"><li v-for="reason in handoffPreview.blockers" :key="reason">{{ reason }}</li></ul>
    </el-dialog>

    <el-dialog v-model="showCreate" title="新建线索" width="600px">
      <el-form :model="form" label-width="100px">
        <el-form-item label="公司名称" required>
          <el-input v-model="form.companyName" />
        </el-form-item>
        <el-form-item label="统一社会信用代码">
          <el-input v-model="form.creditCode" maxlength="18" placeholder="18位，国内企业去重首选" />
        </el-form-item>
        <el-form-item label="官网域名">
          <el-input v-model="form.websiteDomain" placeholder="如 example.com" />
        </el-form-item>
        <el-form-item label="联系人">
          <el-input v-model="form.contactName" />
        </el-form-item>
        <el-form-item label="手机">
          <el-input v-model="form.mobile" />
        </el-form-item>
        <el-form-item label="邮箱">
          <el-input v-model="form.email" />
        </el-form-item>
        <el-form-item label="来源">
          <el-select v-model="form.source">
            <el-option label="存量Excel" :value="0" />
            <el-option label="政府公示" :value="1" />
            <el-option label="行业协会" :value="2" />
            <el-option label="招投标" :value="3" />
            <el-option label="地图" :value="4" />
            <el-option label="自媒体" :value="5" />
            <el-option label="转介绍" :value="6" />
            <el-option label="其他" :value="99" />
          </el-select>
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

    <el-dialog v-model="showConvert" title="转化为客户主档" width="600px">
      <el-form :model="convertForm" label-width="100px">
        <el-form-item label="公司全称" required>
          <el-input v-model="convertForm.fullName" />
        </el-form-item>
        <el-form-item label="统一社会信用代码">
          <el-input v-model="convertForm.creditCode" maxlength="18" />
        </el-form-item>
        <el-form-item label="所在城市" required>
          <el-select v-model="convertForm.city">
            <el-option label="潍坊" :value="0" />
            <el-option label="青岛" :value="1" />
            <el-option label="烟台" :value="2" />
            <el-option label="济南" :value="3" />
            <el-option label="其他" :value="99" />
          </el-select>
        </el-form-item>
        <el-form-item label="所属行业" required>
          <el-select v-model="convertForm.industry">
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
          <el-switch v-model="convertForm.hasWebsite" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showConvert = false">取消</el-button>
        <el-button type="primary" @click="handleConvertConfirm" :loading="converting">确认转化</el-button>
      </template>
    </el-dialog>

    <!-- 批量导入对话框 -->
    <el-dialog v-model="showImport" title="批量导入线索" width="600px">
      <el-alert type="info" :closable="false" style="margin-bottom:12px;">
        <template #title>
          Excel 列名：公司名称、统一社会信用代码、官网域名、联系人、手机、邮箱、来源、备注。公司名称必填，信用代码用于去重。
        </template>
      </el-alert>
      <el-upload
        drag
        :auto-upload="false"
        :on-change="handleFileChange"
        :limit="1"
        accept=".xls,.xlsx,.csv"
      >
        <el-icon class="el-icon--upload"><upload-filled /></el-icon>
        <div class="el-upload__text">将 Excel 文件拖到此处，或<em>点击选择</em></div>
      </el-upload>
      <div v-if="parsedRows.length > 0" style="margin-top:12px;">
        <el-descriptions :column="2" border size="small">
          <el-descriptions-item label="解析行数">{{ parsedRows.length }}</el-descriptions-item>
          <el-descriptions-item label="去重后提交">{{ dedupedRows.length }}</el-descriptions-item>
          <el-descriptions-item label="Excel内重复">{{ parsedRows.length - dedupedRows.length }}</el-descriptions-item>
          <el-descriptions-item label="无效行(缺公司名)" :span="2">{{ invalidRows }}</el-descriptions-item>
        </el-descriptions>
      </div>
      <div v-if="importResult" style="margin-top:12px;">
        <el-result icon="success" title="导入完成" :sub-title="`共${importResult.total}条，新增${importResult.imported}条，重复跳过${importResult.skipped}条，失败${importResult.failed}条`" />
      </div>
      <template #footer>
        <el-button @click="showImport = false">关闭</el-button>
        <el-button type="primary" @click="handleImport" :loading="importing" :disabled="dedupedRows.length === 0">开始导入</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="showAssign" title="分配线索" width="420px">
      <div style="margin-bottom:12px;color:#666;">
        企业：{{ currentLead?.companyName }}
      </div>
      <el-select v-model="assignTarget" placeholder="选择负责人" style="width:100%">
        <el-option v-for="m in teamMembers" :key="m.id" :label="(m.displayName || m.userName)" :value="m.id" />
      </el-select>
      <template #footer>
        <el-button @click="showAssign = false">取消</el-button>
        <el-button type="primary" @click="confirmAssign">确认分配</el-button>
      </template>
    </el-dialog>

    <!-- 标签编辑对话框 -->
    <el-dialog v-model="showTagsDialog" title="编辑标签" width="400px">
      <div style="margin-bottom:12px">
        <el-input
          v-model="tagsInput"
          placeholder="输入标签后按回车添加"
          @keyup.enter="addTag"
        >
          <template #append>
            <el-button @click="addTag">添加</el-button>
          </template>
        </el-input>
      </div>
      <div style="min-height:60px">
        <el-tag
          v-for="tag in tagsList"
          :key="tag"
          closable
          style="margin:4px"
          @close="removeTag(tag)"
        >
          {{ tag }}
        </el-tag>
        <span v-if="tagsList.length === 0" style="color:#909399;font-size:13px">暂无标签</span>
      </div>
      <div style="margin-top:12px;font-size:12px;color:#909399">
        常用标签：
        <el-link v-for="t in ['百度推广','重点客户','已报价','待回访','高意向','无官网','GEO低分']" :key="t" type="primary" style="margin-right:8px" @click="() => { if(!tagsList.includes(t)) tagsList.push(t); }">{{t}}</el-link>
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
import { useRoute, useRouter } from 'vue-router';
import { ElMessage, ElMessageBox } from 'element-plus';
import { Plus, Search, Refresh, Upload, UploadFilled, Download } from '@element-plus/icons-vue';
import { exportToExcel } from '../../utils/exportExcel';
import { listLeads, createLead, deleteLead, claimLead, assignLead, discardLead, convertLead, batchImportLeads, listTeamMembers, updateLeadTags } from '../../api/crm';
import type { LeadDto, CreateLeadRequest, ConvertLeadRequest } from '../../types/crm';
import { LeadStatus, SubjectType, IntentRating, CompanyCity, CompanyIndustry } from '../../types/crm';
import {
  crmEnumNumber, crmLabel, crmTextLabel,
  assessmentCategoryLabels, assessmentStateLabels, businessCategoryForAssessment,
  leadStatusLabels, subjectTypeLabels,
  evidenceTypeLabels, evidenceVerificationLabels, evidenceTypeOptions, evidenceVerificationOptions,
} from '../../utils/crmEnum';
import type { BatchImportResult, TeamMember } from '../../api/crm';
import { readSpreadsheetRows } from '../../utils/spreadsheet';
import { useCrmPermission } from '../../composables/useCrmPermission';
import { apiGet, apiPost, apiPut } from '../../api/http';

interface EvidenceItem {
  id?: string;
  type: string;
  url?: string | null;
  collectedAt?: string | null;
  verificationStatus: string;
  note?: string | null;
  version: number;
}

interface DemandAssessmentRow {
  id?: string;
  category: string;
  state: string;
  confidence?: string;
  /** @deprecated 旧自由文本字段，服务端已停止写入。新数据见 evidenceItems。 */
  evidence?: string;
  evidenceItems: EvidenceItem[];
  reason?: string;
  nextAction?: string;
  reviewedAt?: string;
  version: number;
}

const showAssessment = ref(false);
const router = useRouter();
const assessmentLead = ref<LeadDto | null>(null);
const assessmentRows = ref<DemandAssessmentRow[]>([]);
const assessmentOriginId = ref<number | null>(null);
const savingAssessment = ref<string | null>(null);
const expandedHistoryCategory = ref<string | null>(null);
const assessmentHistory = ref<Record<string, DemandAssessmentRevision[]>>({});
interface VisitHandoffPreview { handoffStatus: string; sourceVerification: string; assessmentConfirmed: boolean; existingDeal: boolean; blockers: string[] }
const showVisitHandoff = ref(false);
const handoffCategory = ref('website');
const handoffKind = ref('visit');
const handoffReference = ref('');
const handoffLoading = ref(false);
const handoffPreview = ref<VisitHandoffPreview | null>(null);
function openVisitHandoff(item: DemandAssessmentRow) {
  handoffCategory.value = item.category;
  handoffKind.value = 'visit';
  handoffReference.value = '';
  handoffPreview.value = null;
  showVisitHandoff.value = true;
}
async function previewVisitHandoff() {
  if (!assessmentLead.value) return;
  handoffLoading.value = true;
  try {
    handoffPreview.value = await apiPost<VisitHandoffPreview>(`/api/v1/crm/leads/${assessmentLead.value.id}/visit-handoff-preview`, {
      category: handoffCategory.value, referenceKind: handoffKind.value, referenceId: handoffReference.value.trim()
    });
  } catch (error: any) { handoffPreview.value = null; ElMessage.error(error?.detail || '交接预检失败'); }
  finally { handoffLoading.value = false; }
}
function assessmentCategoryLabel(category: string) { return crmTextLabel(assessmentCategoryLabels, category, category); }
function assessmentStateLabel(state: string) { return crmTextLabel(assessmentStateLabels, state, state); }
function evidenceTypeLabel(t: string) { return crmTextLabel(evidenceTypeLabels, t, t); }
function evidenceVerificationLabel(v: string) { return crmTextLabel(evidenceVerificationLabels, v, v); }

function addEvidenceItem(item: DemandAssessmentRow) {
  if (!item.evidenceItems) item.evidenceItems = [];
  item.evidenceItems.push({
    type: 'manual',
    verificationStatus: 'not_verified',
    version: item.version,
  });
}

function openDealFromAssessment(item: DemandAssessmentRow) {
  if (!assessmentLead.value?.convertedToCompanyId) {
    ElMessage.info('请先将线索转为客户，再创建商机。');
    showAssessment.value = false;
    return;
  }
  const category = businessCategoryForAssessment(item.category);
  if (category === null) {
    ElMessage.warning(`评估品类「${item.category}」没有对应的业务品类，无法建商机`);
    return;
  }
  router.push({ path: '/crm/deals', query: {
    action: 'create', companyId: String(assessmentLead.value.convertedToCompanyId),
    category: String(category), suggestion: assessmentCategoryLabel(item.category),
    sourceAssessmentId: item.id, sourceVersion: String(item.version)
  } });
  showAssessment.value = false;
}

interface DemandAssessmentRevision extends DemandAssessmentRow { reviewerId: number; reviewedAt: string; }

async function toggleAssessmentHistory(category: string) {
  if (expandedHistoryCategory.value === category) { expandedHistoryCategory.value = null; return; }
  if (!assessmentLead.value) return;
  try {
    assessmentHistory.value[category] = await apiGet<DemandAssessmentRevision[]>(
      `/api/v1/crm/leads/${assessmentLead.value.id}/demand-assessments/${category}/history`);
    expandedHistoryCategory.value = category;
  } catch {
    ElMessage.error('评估历史加载失败');
  }
}

async function openAssessment(lead: LeadDto) {
  assessmentLead.value = lead;
  try {
    const data = await apiGet<{ sourceReverseLookupRecordId?: number; items: DemandAssessmentRow[] }>(`/api/v1/crm/leads/${lead.id}/demand-assessments`);
    assessmentRows.value = data.items;
    assessmentOriginId.value = data.sourceReverseLookupRecordId ?? null;
    expandedHistoryCategory.value = null;
    assessmentHistory.value = {};
    showAssessment.value = true;
  } catch {
    ElMessage.error('需求评估加载失败');
  }
}

async function saveAssessment(item: DemandAssessmentRow) {
  if (!assessmentLead.value) return;
  const items = item.evidenceItems ?? [];
  // The server rule, mirrored: 可能/已确认 needs at least one STRUCTURED
  // evidence row. Checking only "something was typed" was the old contract.
  if (['potential', 'confirmed'].includes(item.state) && items.length === 0) {
    ElMessage.warning('可能或已确认需求必须至少添加一条证据');
    return;
  }
  savingAssessment.value = item.category;
  try {
    const data = await apiPut<{ items: DemandAssessmentRow[] }>(
      `/api/v1/crm/leads/${assessmentLead.value.id}/demand-assessments/${item.category}`,
      { state: item.state, confidence: item.confidence || null, evidence: null,
        evidenceItems: items.map(e => ({
          evidenceType: e.type,
          url: e.url || null,
          collectedAt: e.collectedAt || null,
          verificationStatus: e.verificationStatus,
          note: e.note || null,
        })),
        reason: item.reason || null, nextAction: item.nextAction || null });
    assessmentRows.value = data.items;
    ElMessage.success('需求评估已保存');
    if (expandedHistoryCategory.value === item.category) {
      try {
        assessmentHistory.value[item.category] = await apiGet<DemandAssessmentRevision[]>(
          `/api/v1/crm/leads/${assessmentLead.value.id}/demand-assessments/${item.category}/history`);
      } catch {
        ElMessage.warning('评估已保存，历史记录刷新失败');
      }
    }
  } catch {
    ElMessage.error('需求评估保存失败');
  } finally {
    savingAssessment.value = null;
  }
}

const route = useRoute();
const can = useCrmPermission();

// 行业自动推断：根据企业名称关键词
function inferIndustry(companyName: string, explicitIndustry?: string): number {
  // Excel 中有明确行业列时优先使用
  if (explicitIndustry) {
    const map: Record<string, number> = {
      '机械': 0, '机械制造': 0, '设备': 0,
      '纺织': 1, '服装': 1,
      '食品': 2, '食品加工': 2, '餐饮': 2,
      '化工': 3, '化学': 3,
      '建材': 4, '建筑材料': 4,
      '农业': 5, '种植': 5, '养殖': 5,
      '商贸': 6, '贸易': 6, '进出口': 6,
      '服务': 7, '服务业': 7, '科技': 7,
    };
    for (const [key, val] of Object.entries(map)) {
      if (explicitIndustry.includes(key)) return val;
    }
  }
  const name = companyName;
  const rules: Array<{ keywords: string[]; industry: number }> = [
    { keywords: ['食品', '餐饮', '粮油', '饮料', '调味', '肉食', '水产', '乳业', '烘焙', '糖果', '茶叶', '农产品加工'], industry: 2 },
    { keywords: ['机械', '设备', '重工', '机床', '泵阀', '轴承', '齿轮', '电机', '模具', '五金', '铸造', '锻造'], industry: 0 },
    { keywords: ['纺织', '服装', '印染', '面料', '纱线', '家纺', '针织', '制衣'], industry: 1 },
    { keywords: ['化工', '化学', '涂料', '塑料', '橡胶', '化肥', '农药', '新材料', '精细化工'], industry: 3 },
    { keywords: ['建材', '水泥', '陶瓷', '玻璃', '石材', '保温', '装饰', '门窗', '管材'], industry: 4 },
    { keywords: ['农业', '种植', '养殖', '苗木', '果蔬', '畜牧', '饲料', '种业'], industry: 5 },
    { keywords: ['贸易', '商贸', '进出口', '销售', '经销', '供应链', '商贸有限公司'], industry: 6 },
    { keywords: ['服务', '咨询', '物流', '科技', '网络', '传媒', '广告', '软件', '信息'], industry: 7 },
  ];
  for (const rule of rules) {
    if (rule.keywords.some(k => name.includes(k))) return rule.industry;
  }
  return 99; // 其他
}

const loading = ref(false);
const creating = ref(false);
const converting = ref(false);
const list = ref<LeadDto[]>([]);
const total = ref(0);
const showCreate = ref(false);
const showConvert = ref(false);
const showImport = ref(false);
const currentLead = ref<LeadDto | null>(null);
const importing = ref(false);
const parsedRows = ref<CreateLeadRequest[]>([]);
const dedupedRows = ref<CreateLeadRequest[]>([]);
const invalidRows = ref(0);
const importResult = ref<BatchImportResult | null>(null);
const teamMembers = ref<TeamMember[]>([]);
const assignTarget = ref<number | null>(null);
const showAssign = ref(false);

const filters = reactive({
  keyword: '',
  status: undefined as number | undefined,
  rating: undefined as number | undefined,
  ownerId: undefined as number | undefined,
  subjectType: undefined as number | undefined,
  tag: '',
  page: 1,
  pageSize: 20
});

// 标签编辑
const showTagsDialog = ref(false);
const tagsEditLead = ref<LeadDto | null>(null);
const tagsInput = ref('');
const tagsList = ref<string[]>([]);

// 当前登录用户 id（用于"我的线索"过滤）
const currentUserId = ref<number | null>(null);

function ownerName(ownerId?: number | null): string {
  if (!ownerId) return '-';
  const m = teamMembers.value.find(x => x.id === Number(ownerId));
  return m ? (m.displayName || m.userName) : `#${ownerId}`;
}

async function handleAssign(row: LeadDto) {
  currentLead.value = row;
  assignTarget.value = row.ownerId ? Number(row.ownerId) : null;
  showAssign.value = true;
}

function handleEditTags(row: LeadDto) {
  tagsEditLead.value = row;
  tagsList.value = [...(row.tags || [])];
  tagsInput.value = '';
  showTagsDialog.value = true;
}

function addTag() {
  const t = tagsInput.value.trim();
  if (t && !tagsList.value.includes(t)) {
    tagsList.value.push(t);
  }
  tagsInput.value = '';
}

function removeTag(tag: string) {
  tagsList.value = tagsList.value.filter(t => t !== tag);
}

async function saveTags() {
  if (!tagsEditLead.value) return;
  try {
    await updateLeadTags(String(tagsEditLead.value.id), tagsList.value);
    tagsEditLead.value.tags = [...tagsList.value];
    ElMessage.success('标签已更新');
    showTagsDialog.value = false;
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || '更新失败');
  }
}

async function confirmAssign() {
  if (!currentLead.value || !assignTarget.value) {
    ElMessage.warning('请选择负责人');
    return;
  }
  try {
    await assignLead(currentLead.value.id, assignTarget.value);
    ElMessage.success('分配成功');
    showAssign.value = false;
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '分配失败');
  }
}

function filterMine() {
  if (currentUserId.value) {
    filters.ownerId = filters.ownerId === currentUserId.value ? undefined : currentUserId.value;
    filters.page = 1;
    loadData();
  }
}

const form = reactive<CreateLeadRequest>({
  companyName: '',
  creditCode: '',
  websiteDomain: '',
  contactName: '',
  mobile: '',
  email: '',
  source: 99,
  remarks: ''
});

const convertForm = reactive<ConvertLeadRequest>({
  leadId: '',
  fullName: '',
  creditCode: '',
  city: 0,
  industry: 0,
  hasWebsite: false
});

async function loadData() {
  loading.value = true;
  try {
    const result = await listLeads(filters);
    list.value = result.items;
    total.value = result.total;
  } catch (e: any) {
    ElMessage.error(e.message || '加载失败');
  } finally {
    loading.value = false;
  }
}

function resetFilters() {
  filters.keyword = '';
  filters.status = undefined;
  filters.rating = undefined;
  filters.ownerId = undefined;
  filters.tag = '';
  filters.page = 1;
  loadData();
}

onMounted(async () => {
  if (typeof route.query.keyword === 'string') {
    filters.keyword = route.query.keyword;
  }
  try {
    teamMembers.value = await listTeamMembers();
    // 从 localStorage 或 auth 获取当前用户 id
    const auth = localStorage.getItem('auth');
    if (auth) {
      const parsed = JSON.parse(auth);
      currentUserId.value = parsed.id || parsed.userId || null;
    }
  } catch { /* 忽略 */ }
  loadData();
});

function handleExport() {
  if (list.value.length === 0) { ElMessage.warning('没有可导出的数据'); return; }
  exportToExcel(list.value, [
    { key: 'companyName', label: '公司名称' },
    { key: 'subjectType', label: '主体类型' },
    { key: 'industry', label: '行业' },
    { key: 'contactName', label: '联系人' },
    { key: 'mobile', label: '手机' },
    { key: 'phone', label: '电话' },
    { key: 'email', label: '邮箱' },
    { key: 'websiteDomain', label: '官网' },
    { key: 'address', label: '地址' },
    { key: 'status', label: '状态' },
    { key: 'rating', label: '评级' },
    { key: 'aiScore', label: 'AI评分' },
    { key: 'source', label: '来源' },
    { key: 'ownerId', label: '负责人ID' },
    { key: 'createdAt', label: '创建时间' },
    { key: 'remarks', label: '备注' },
  ], '线索池导出');
  ElMessage.success(`已导出 ${list.value.length} 条线索`);
}

async function handleCreate() {
  if (!form.companyName.trim()) {
    ElMessage.warning('请输入公司名称');
    return;
  }
  creating.value = true;
  try {
    await createLead(form);
    ElMessage.success('创建成功');
    showCreate.value = false;
    Object.assign(form, { companyName: '', creditCode: '', websiteDomain: '', contactName: '', mobile: '', email: '', source: 99, remarks: '' });
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '创建失败');
  } finally {
    creating.value = false;
  }
}

async function handleClaim(row: LeadDto) {
  try {
    await claimLead(row.id);
    ElMessage.success('认领成功');
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '认领失败');
  }
}

function handleConvert(row: LeadDto) {
  currentLead.value = row;
  convertForm.leadId = row.id;
  convertForm.fullName = row.companyName;
  convertForm.creditCode = row.creditCode || '';
  convertForm.city = row.city == null ? CompanyCity.Weifang : crmEnumNumber(row.city, CompanyCity);
  convertForm.industry = row.industry == null ? inferIndustry(row.companyName) : crmEnumNumber(row.industry, CompanyIndustry);
  convertForm.hasWebsite = Boolean(row.websiteDomain);
  convertForm.remarks = row.remarks || '';
  showConvert.value = true;
}

async function handleConvertConfirm() {
  if (!convertForm.fullName.trim()) {
    ElMessage.warning('请输入公司全称');
    return;
  }
  converting.value = true;
  try {
    await convertLead(currentLead.value!.id, convertForm);
    ElMessage.success('转化成功，已创建客户主档');
    showConvert.value = false;
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '转化失败');
  } finally {
    converting.value = false;
  }
}

async function handleDiscard(row: LeadDto) {
  try {
    await ElMessageBox.confirm('确认废弃该线索？', '提示', { type: 'warning' });
    await discardLead(row.id);
    ElMessage.success('已废弃');
    loadData();
  } catch { /* cancelled */ }
}

async function handleDelete(row: LeadDto) {
  try {
    await ElMessageBox.confirm('确认删除该线索？', '提示', { type: 'warning' });
    await deleteLead(row.id);
    ElMessage.success('删除成功');
    loadData();
  } catch { /* cancelled */ }
}

// ===== 批量导入 =====
function normalizeCreditCode(code?: string): string {
  return (code || '').trim().toUpperCase().replace(/\s/g, '');
}

async function handleFileChange(file: any) {
  importResult.value = null;
  try {
    const json = await readSpreadsheetRows(file.raw);
      const rows: CreateLeadRequest[] = [];
      let invalid = 0;
      for (const r of json) {
        const companyName = String(r['公司名称'] || r['companyName'] || r['企业名称'] || '').trim();
        if (!companyName) { invalid++; continue; }
        const explicitIndustry = String(r['行业'] || r['所属行业'] || r['industry'] || '').trim();
        rows.push({
          companyName,
          creditCode: normalizeCreditCode(String(r['统一社会信用代码'] || r['creditCode'] || r['信用代码'] || '')),
          websiteDomain: String(r['官网域名'] || r['websiteDomain'] || r['官网'] || '').trim().toLowerCase(),
          contactName: String(r['联系人'] || r['contactName'] || '').trim(),
          mobile: String(r['手机'] || r['mobile'] || r['联系电话'] || '').trim(),
          email: String(r['邮箱'] || r['email'] || '').trim(),
          industry: inferIndustry(companyName, explicitIndustry),
          source: 0, // 存量Excel
          remarks: String(r['备注'] || r['remarks'] || '').trim()
        });
      }
      parsedRows.value = rows;
      invalidRows.value = invalid;
      // 前端预去重：信用代码 > 公司名 > 域名 > 手机
      const seen = new Set<string>();
      const deduped: CreateLeadRequest[] = [];
      for (const row of rows) {
        let key = '';
        if (row.creditCode) key = 'cc:' + row.creditCode;
        else if (row.companyName) key = 'name:' + row.companyName;
        else if (row.websiteDomain) key = 'dom:' + row.websiteDomain;
        else if (row.mobile) key = 'mob:' + row.mobile;
        else key = 'row:' + Math.random();
        if (!seen.has(key)) { seen.add(key); deduped.push(row); }
      }
      dedupedRows.value = deduped;
      ElMessage.success(`解析成功：${rows.length}条有效，去重后${deduped.length}条`);
  } catch (err: any) {
    ElMessage.error('Excel解析失败：' + (err.message || '格式错误'));
  }
}

async function handleImport() {
  if (dedupedRows.value.length === 0) { ElMessage.warning('没有可导入的数据'); return; }
  importing.value = true;
  try {
    const result = await batchImportLeads(dedupedRows.value);
    importResult.value = result;
    ElMessage.success(`导入完成：新增${result.imported}条，跳过${result.skipped}条`);
    loadData();
  } catch (e: any) {
    ElMessage.error(e.message || '导入失败');
  } finally {
    importing.value = false;
  }
}

function statusLabel(s: number) { return crmLabel(leadStatusLabels, s, LeadStatus); }
function statusTagType(s: number) { return ['info', 'warning', 'success', 'danger'][crmEnumNumber(s, LeadStatus)] || 'info'; }
function subjectTypeLabel(s: number) {
  return crmLabel(subjectTypeLabels, s, SubjectType, '其他');
}
function subjectTypeTagType(s: number) {
  s = crmEnumNumber(s, SubjectType);
  // 企业+教培+个体商户=success(绿)，渠道机会=warning(橙)，学历教育+事业单位+政府=info(灰)
  if (s === 0 || s === 8 || s === 10) return 'success';
  if (s >= 1 && s <= 4) return 'warning';
  return 'info';
}
function ratingLabel(r: number) { return ['A', 'B', 'C', 'D'][crmEnumNumber(r, IntentRating)] || '-'; }
function ratingTagType(r: number) { return ['success', 'warning', 'info', 'danger'][crmEnumNumber(r, IntentRating)] || 'info'; }
function scoreTagType(score?: number) {
  if (score === undefined) return 'info';
  if (score >= 70) return 'success';
  if (score >= 50) return 'warning';
  return 'info';
}
function formatDate(d: string) { return new Date(d).toLocaleString('zh-CN'); }
</script>

<style scoped>
.crm-lead-list { padding: 16px; }
.card-header { display: flex; justify-content: space-between; align-items: center; }
.title { font-size: 16px; font-weight: 600; }
.search-form { margin-bottom: 16px; }
.pagination { margin-top: 16px; display: flex; justify-content: flex-end; }
.assessment-origin { color: #606266; font-size: 12px; }
.handoff-controls { display: flex; flex-wrap: wrap; gap: 8px; margin: 14px 0; }
.assessment-row { border-bottom: 1px solid #ebeef5; padding: 14px 0; }
.assessment-row-header { display: flex; justify-content: space-between; margin-bottom: 8px; }
.assessment-row-header span { color: #909399; font-size: 12px; }
</style>
