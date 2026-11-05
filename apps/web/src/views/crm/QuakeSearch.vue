<template>
  <div class="quake-search-page">
    <el-card>
      <template #header>
        <div class="card-header">
          <span>Quake 线索采集</span>
          <span class="hint">从 360 Quake 搜索候选资产，先留存核验，再决定是否转入线索池</span>
        </div>
      </template>

      <section class="quake-configuration" aria-label="Quake 连接配置">
        <div class="configuration-status">
          <strong>Quake 连接</strong>
          <el-tag v-if="configLoading" type="info">检查中</el-tag>
          <el-tag v-else :type="keyConfigured ? 'success' : 'warning'">
            {{ keyConfigured ? '已配置' : '未配置' }}
          </el-tag>
          <span v-if="keySource === 'runtime'" class="hint">当前 API 进程有效，重启后需重新填写</span>
          <span v-else-if="keySource === 'environment'" class="hint">由服务端环境配置</span>
          <span v-if="runtimeKeys.length" class="hint">备用 Key 共 {{ runtimeKeys.length }} 个；认证或额度受限时自动切换</span>
        </div>
        <el-alert v-if="configError" :title="configError" type="error" :closable="false" show-icon />
        <el-alert v-else-if="!configLoading && !keyConfigured" title="请先到数据源设置配置 Quake Key，才能发起搜索。" type="warning" :closable="false" show-icon />
        <div class="configuration-form"><el-button @click="$router.push('/crm/data-sources')">打开数据源设置</el-button></div>
      </section>

      <!-- 搜索条件 -->
      <div class="search-bar">
        <el-select v-model="preset" placeholder="快捷查询" style="width:180px" @change="applyPreset">
          <el-option label="潍坊 食品企业" value="wf_food" />
          <el-option label="潍坊 机械制造" value="wf_machinery" />
          <el-option label="潍坊 化工企业" value="wf_chemical" />
          <el-option label="潍坊 纺织服装" value="wf_textile" />
          <el-option label="潍坊 无HTTPS站点" value="wf_nohttps" />
          <el-option label="自定义" value="custom" />
        </el-select>
        <el-input v-model="query" placeholder='Quake 查询语法，如 city_cn: "潍坊" AND title: "食品"' style="flex:1;min-width:300px" />
        <el-input-number v-model="size" :min="5" :max="50" :step="5" style="width:110px" />
        <el-button type="primary" :loading="searching" :disabled="!keyConfigured" @click="doSearch">搜索</el-button>
        <el-button v-if="lastQueryInfo" type="warning" @click="doForceSearch" :loading="searching" :disabled="!keyConfigured">
          强制重新查询
        </el-button>
      </div>

      <section class="file-import" aria-label="Quake 导出文件预检">
        <strong>导出文件预检</strong>
        <span class="hint">支持 Quake JSON、仅含一个 JSON 的 ZIP、Excel；仅解析与预检，不会自动创建线索。</span>
        <input type="file" accept=".json,.zip,.xlsx,.xls" aria-label="选择 Quake 导出文件" :disabled="fileLoading" @change="loadExportFile" />
        <el-button :disabled="fileRows.length === 0 || fileLoading" :loading="fileLoading" @click="openAssetPreview(fileRows)">
          预检文件候选 ({{ fileRows.length }})
        </el-button>
        <span v-if="fileName" class="hint">{{ fileName }}；缺少标题 {{ fileMissingTitles }} 条</span>
      </section>

      <!-- 冷却期提示 -->
      <el-alert v-if="cooldownMessage" :title="cooldownMessage" type="warning" :closable="false" show-icon style="margin-top:10px" />

      <!-- 搜索结果 -->
      <div v-if="results.length > 0" class="results-area">
        <div class="results-toolbar">
          <span>共 {{ results.length }} 条结果，已选 {{ selected.length }} 条
            <span v-if="enrichedCount > 0" style="color:#67c23a"> | ICP补全 {{ enrichedCount }} 家</span>
            <span v-if="checkedCount > 0" style="color:#e6a23c"> | 检测 {{ checkedCount }} 站</span>
          </span>
          <div>
            <el-button type="warning" :disabled="results.length === 0" :loading="enriching" @click="doEnrich">
              ICP补全
            </el-button>
            <el-button type="primary" :disabled="results.length === 0" :loading="checking" @click="doWebsiteCheck">
              网站检测
            </el-button>
            <el-button type="info" :disabled="results.length === 0" :loading="geoChecking" @click="doGeoCheck">
              GEO检测
            </el-button>
            <el-button type="danger" :disabled="issueSites.length === 0" :loading="signaling" @click="doCreateSignals">
              问题转信号 ({{ issueSites.length }})
            </el-button>
            <el-button type="success" :disabled="selected.length === 0" @click="openAssetPreview(selected)">
              留存选中候选 ({{ selected.length }})
            </el-button>
            <el-button type="warning" :disabled="results.length === 0" @click="openAssetPreview(results)">
              留存全部候选 ({{ results.length }})
            </el-button>
          </div>
        </div>
        <el-table :data="results" @selection-change="onSelect" style="margin-top:10px" max-height="500">
          <el-table-column type="selection" width="45" />
          <el-table-column prop="domain" label="域名/IP" min-width="140">
            <template #default="{ row }">
              <el-link v-if="safeExternalHttpUrl('http://' + row.domain)" :href="safeExternalHttpUrl('http://' + row.domain)" target="_blank" rel="noopener noreferrer" type="primary">{{ row.domain }}</el-link>
              <span v-else>{{ row.domain }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="title" label="网站标题" min-width="140" show-overflow-tooltip />
          <el-table-column label="ICP企业名称" min-width="160" show-overflow-tooltip>
            <template #default="{ row }">
              <span v-if="row.icpUnitName" style="color:#67c23a">{{ row.icpUnitName }}</span>
              <span v-else-if="row.icpStatus === 'not_found'" style="color:#909399">未查到备案记录</span>
              <span v-else-if="row.icpStatus === 'error'" style="color:#f56c6c">查询失败</span>
              <span v-else style="color:#909399">待补全</span>
            </template>
          </el-table-column>
          <el-table-column label="检测结果" width="120">
            <template #default="{ row }">
              <el-tag v-if="!row.siteChecked" type="info" size="small">未检测</el-tag>
              <template v-else>
                <el-tag :type="row.siteError || row.siteIssues?.length ? 'danger' : 'success'" size="small">
                  {{ row.siteError ? '检测未完成' : row.siteAccessible ? (row.siteIssues?.length || 0) + '个问题' : '无法访问' }}
                </el-tag>
              </template>
            </template>
          </el-table-column>
          <el-table-column label="检测问题" min-width="200">
            <template #default="{ row }">
              <el-popover v-if="row.siteIssues?.length" placement="top" :width="300" trigger="click">
                <template #reference>
                  <el-link type="danger" size="small">{{ row.siteIssues[0] }}<span v-if="row.siteIssues.length>1"> 等{{row.siteIssues.length}}项</span></el-link>
                </template>
                <div style="font-size:13px">
                  <p v-for="(iss,i) in row.siteIssues" :key="i" style="margin:4px 0;color:#f56c6c">• {{iss}}</p>
                  <p style="margin:6px 0 0;color:#909399;font-size:12px">HTTPS:{{row.siteHttps?'是':'否'}} | 移动端:{{row.siteViewport?'适配':'不适配'}} | 响应:{{row.siteMs}}ms</p>
                </div>
              </el-popover>
              <span v-else-if="row.siteError" style="color:#f56c6c;font-size:12px">{{ row.siteError }}</span>
              <span v-else-if="row.siteChecked" style="color:#67c23a;font-size:12px">正常</span>
            </template>
          </el-table-column>
          <el-table-column label="百度推广" width="100">
            <template #default="{ row }">
              <el-tag v-if="row.hasBaiduAds" type="warning" size="small" effect="dark">
                {{ row.baiduAdsType || '百度推广' }}
              </el-tag>
              <span v-else-if="row.siteChecked" style="color:#909399;font-size:12px">未检测到</span>
              <span v-else style="color:#c0c4cc;font-size:12px">-</span>
            </template>
          </el-table-column>
          <el-table-column label="GEO评分" width="110">
            <template #default="{ row }">
              <el-tag v-if="!row.geoChecked" type="info" size="small">未检测</el-tag>
              <el-tag v-else :type="geoBandType(row.geoBand)" size="small">
                {{ row.geoScore }}分 {{ row.geoBand }}
              </el-tag>
            </template>
          </el-table-column>
        </el-table>
      </div>

      <el-empty v-else-if="!searching" description="输入查询条件开始搜索" />
    </el-card>
    <el-dialog v-model="previewVisible" title="确认留存 Quake 候选资产" width="780px">
      <el-alert title="此操作只保存待审资产，不创建 CRM 线索或客户。域名、网站标题和 ICP 信息都不能自动证明企业归属。" type="warning" :closable="false" show-icon />
      <p class="hint">共 {{ previewRows.length }} 条；库内已有 {{ previewExistingCount }} 条，批内重复 {{ previewDuplicateCount }} 条。请核对域名/IP 与标题后提交。</p>
      <el-alert v-if="previewLoading" title="正在核对已有资产和清洗判定…" type="info" :closable="false" />
      <el-table :data="previewRows.slice((previewPage - 1) * 100, previewPage * 100)" max-height="360" style="width:100%">
        <el-table-column prop="domain" label="域名/IP" min-width="170" />
        <el-table-column label="网站标题（仅供参考）" min-width="200" show-overflow-tooltip><template #default="{ row }">{{ row.title || '未提供' }}</template></el-table-column>
        <el-table-column prop="icpUnitName" label="ICP 候选主体" min-width="180" show-overflow-tooltip />
        <el-table-column label="预检提示" width="130">
          <template #default="{ row }">
            <el-tag v-if="row.duplicateInBatch" type="warning" size="small">批内重复</el-tag>
            <el-tag v-else-if="row.existingAssetId" type="info" size="small">库内已有</el-tag>
            <el-tag v-else-if="!row.domain" type="danger" size="small">缺少目标</el-tag>
            <el-tag v-else :type="row.cleanVerdict === 'rejected' ? 'danger' : 'warning'" size="small">
              {{ row.cleanVerdict === 'rejected' ? '低质量候选' : '待核验' }}
            </el-tag>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination v-if="previewRows.length > 100" v-model:current-page="previewPage" :page-size="100" :total="previewRows.length" layout="prev, pager, next" style="margin-top:10px" />
      <template #footer>
        <el-button @click="previewVisible = false">取消</el-button>
        <el-button type="primary" :loading="savingAssets" :disabled="previewLoading || pendingRows.length === 0" @click="saveAssetCandidates">确认留存候选 ({{ pendingRows.length }})</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { ElMessage } from 'element-plus';
import { apiGet, apiPost } from '../../api/http';
import { readQuakeExport, type QuakeExportAsset } from './quakeExport';
import { safeExternalHttpUrl } from '../../utils/externalUrl';

interface QuakeSite {
  domain: string;
  ip?: string;
  port?: number;
  title?: string;
  service?: string;
  city?: string;
  province?: string;
  isp?: string;
  os?: string;
  icpUnitName?: string;
  icpLicence?: string;
  icpQueried?: boolean;
  icpStatus?: 'found' | 'not_found' | 'error';
  siteChecked?: boolean;
  siteAccessible?: boolean;
  siteHttps?: boolean;
  siteViewport?: boolean;
  siteMs?: number;
  siteIssues?: string[];
  siteError?: string;
  hasBaiduAds?: boolean;
  baiduAdsType?: string;
  geoChecked?: boolean;
  geoScore?: number;
  geoBand?: string;
  geoError?: string;
  assetInput?: QuakeExportAsset;
}

const preset = ref('wf_food');
const configLoading = ref(true);
const configError = ref('');
const keyConfigured = ref(false);
const keySource = ref('none');
const runtimeKeys = ref<{ id: string; order: number }[]>([]);
const query = ref('city_cn: "潍坊" AND title: "食品"');
const size = ref(20);
const searching = ref(false);
const cooldownMessage = ref('');
const lastQueryInfo = ref<any>(null);
const savingAssets = ref(false);
const previewVisible = ref(false);
type PreviewRow = QuakeSite & { duplicateInBatch: boolean; existingAssetId?: string | number | null; cleanVerdict?: string };
const previewRows = ref<PreviewRow[]>([]);
const previewLoading = ref(false);
const previewPage = ref(1);
const previewBatchId = ref('');
const previewDuplicateCount = computed(() => previewRows.value.filter(row => row.duplicateInBatch).length);
const previewExistingCount = computed(() => previewRows.value.filter(row => !!row.existingAssetId && !row.duplicateInBatch).length);
const pendingRows = computed(() => previewRows.value.filter(row => !row.duplicateInBatch && !row.existingAssetId && !!row.domain));
const enriching = ref(false);
const checking = ref(false);
const geoChecking = ref(false);
const signaling = ref(false);
const results = ref<QuakeSite[]>([]);
const selected = ref<QuakeSite[]>([]);
const fileRows = ref<QuakeSite[]>([]);
const fileName = ref('');
const fileLoading = ref(false);
const fileMissingTitles = computed(() => fileRows.value.filter(row => !row.title).length);

async function loadExportFile(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  fileRows.value = [];
  fileName.value = '';
  if (!file) return;
  fileLoading.value = true;
  try {
    const assets = await readQuakeExport(file);
    fileRows.value = assets.map(asset => ({
      domain: asset.domain || asset.hostname || asset.ipAddress || '',
      ip: asset.ipAddress || undefined, port: asset.port || undefined,
      title: asset.title || undefined, service: asset.serviceName || undefined,
      city: asset.cityCn || undefined, province: asset.provinceCn || undefined,
      assetInput: asset
    }));
    fileName.value = file.name;
    ElMessage.success(`已解析 ${assets.length} 条 Quake 记录，请预检后再留存。`);
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '文件解析失败');
  } finally {
    fileLoading.value = false;
    input.value = '';
  }
}

const enrichedCount = computed(() => results.value.filter(r => r.icpUnitName).length);
const checkedCount = computed(() => results.value.filter(r => r.siteChecked).length);
const issueSites = computed(() => results.value.filter(r => r.siteChecked && r.siteIssues && r.siteIssues.length > 0));

interface QuakeConfigurationStatus {
  configured: boolean;
  source: string;
  persistent: boolean;
  runtimeKeys: { id: string; order: number }[];
}

async function loadConfiguration() {
  configLoading.value = true;
  configError.value = '';
  try {
    const status = await apiGet<QuakeConfigurationStatus>('/api/v1/crm/quake/configuration');
    keyConfigured.value = status.configured;
    keySource.value = status.source;
    runtimeKeys.value = status.runtimeKeys || [];
  } catch {
    keyConfigured.value = false;
    configError.value = '无法读取 Quake 配置状态，请检查服务是否已更新。';
  } finally {
    configLoading.value = false;
  }
}

onMounted(loadConfiguration);

function geoBandType(band?: string): string {
  switch (band) {
    case 'excellent': return 'success';
    case 'good': return 'success';
    case 'foundation': return 'warning';
    case 'critical': return 'danger';
    default: return 'info';
  }
}

const presets: Record<string, string> = {
  wf_food: 'city_cn: "潍坊" AND title: "食品"',
  wf_machinery: 'city_cn: "潍坊" AND title: "机械"',
  wf_chemical: 'city_cn: "潍坊" AND title: "化工"',
  wf_textile: 'city_cn: "潍坊" AND title: "纺织"',
  wf_nohttps: 'city_cn: "潍坊" AND service: "http"',
  custom: ''
};

function applyPreset(val: string) {
  if (val !== 'custom' && presets[val]) {
    query.value = presets[val];
  }
}

function onSelect(rows: QuakeSite[]) {
  selected.value = rows;
}

async function doSearch() {
  if (!query.value.trim()) {
    ElMessage.warning('请输入查询条件');
    return;
  }
  searching.value = true;
  results.value = [];
  cooldownMessage.value = '';
  lastQueryInfo.value = null;
  try {
    const res: any = await apiGet('/api/v1/crm/quake/search', {
      params: { query: query.value, size: size.value }
    });
    if (res.fromCache) {
      // 冷却期内，显示提示
      cooldownMessage.value = res.message || '该查询条件在30天内已查询过，可点击"强制重新查询"跳过冷却期';
      lastQueryInfo.value = { queriedAt: res.lastQueriedAt, resultCount: res.total };
      ElMessage.warning('该条件在冷却期内，已跳过查询以节省额度');
    } else {
      results.value = res.items || [];
      ElMessage.success(`找到 ${results.value.length} 条结果`);
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || '搜索失败，请检查查询语法');
  } finally {
    searching.value = false;
  }
}

async function doForceSearch() {
  if (!query.value.trim()) return;
  searching.value = true;
  results.value = [];
  cooldownMessage.value = '';
  lastQueryInfo.value = null;
  try {
    const res: any = await apiGet('/api/v1/crm/quake/search', {
      params: { query: query.value, size: size.value, force: true }
    });
    results.value = res.items || [];
    ElMessage.success(`强制查询完成，找到 ${results.value.length} 条结果`);
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || '搜索失败');
  } finally {
    searching.value = false;
  }
}

async function doEnrich() {
  if (results.value.length === 0) return;
  enriching.value = true;
  try {
    const domains = results.value.map(r => r.domain);
    const res = await apiPost<{ total: number; found: number; items: any[] }>('/api/v1/crm/icp/lookup', domains);
    const unavailable = res.items.filter(icp => icp.queryStatus === 'disabled' || icp.queryStatus === 'not_configured');
    if (unavailable.length === res.total && res.total > 0) {
      ElMessage.warning(unavailable[0]?.error || 'ICP 查询尚未启用。');
      return;
    }
    // 将 ICP 结果回填到结果列表
    for (const icp of res.items) {
      const idx = results.value.findIndex(r => r.domain === icp.domain);
      if (idx >= 0) {
        results.value[idx].icpQueried = icp.queryStatus !== 'disabled' && icp.queryStatus !== 'not_configured';
        results.value[idx].icpStatus = icp.queryStatus;
        if (icp.found) {
          results.value[idx].icpUnitName = icp.unitName;
          results.value[idx].icpLicence = icp.serviceLicence;
        }
      }
    }
    const failed = res.items.filter(icp => icp.queryStatus === 'error').length;
    ElMessage.success(`补全完成：查到 ${res.found} 家，未查到记录 ${res.total - res.found - failed - unavailable.length} 家，查询失败 ${failed} 家，未启用 ${unavailable.length} 家`);
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || 'ICP补全失败');
  } finally {
    enriching.value = false;
  }
}

async function doWebsiteCheck() {
  if (results.value.length === 0) return;
  checking.value = true;
  try {
    const domains = results.value.map(r => r.domain);
    const res = await apiPost<{ total: number; accessible: number; hasIssues: number; items: any[] }>('/api/v1/crm/website-check', domains);
    for (const chk of res.items) {
      const idx = results.value.findIndex(r => r.domain === chk.domain);
      if (idx >= 0) {
        results.value[idx].siteChecked = true;
        results.value[idx].siteAccessible = chk.accessible;
        results.value[idx].siteHttps = chk.httpsSupported;
        results.value[idx].siteViewport = chk.hasViewport;
        results.value[idx].siteMs = chk.responseMs;
        results.value[idx].siteIssues = chk.issues || [];
        results.value[idx].siteError = chk.error;
        results.value[idx].hasBaiduAds = chk.hasBaiduAds || false;
        results.value[idx].baiduAdsType = chk.baiduAdsType;
      }
    }
    ElMessage.success(`检测完成：可访问 ${res.accessible}/${res.total}，${res.hasIssues} 站有问题`);
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || '网站检测失败');
  } finally {
    checking.value = false;
  }
}

async function doGeoCheck() {
  if (results.value.length === 0) return;
  geoChecking.value = true;
  try {
    const domains = results.value.map(r => r.domain);
    const res = await apiPost<{ total: number; geoChecked: number; critical: number; items: any[] }>('/api/v1/crm/geo-check', domains);
    for (const g of res.items) {
      const idx = results.value.findIndex(r => r.domain === g.domain);
      if (idx >= 0) {
        results.value[idx].geoChecked = g.checked;
        results.value[idx].geoScore = g.checked && !g.error ? g.score : undefined;
        results.value[idx].geoBand = g.checked && !g.error ? g.band : undefined;
        results.value[idx].geoError = g.error;
      }
    }
    ElMessage.success(`GEO检测完成：${res.geoChecked}/${res.total} 站，${res.critical} 站急需优化`);
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || 'GEO检测失败');
  } finally {
    geoChecking.value = false;
  }
}

async function doCreateSignals() {
  if (issueSites.value.length === 0) return;
  signaling.value = true;
  let created = 0;
  try {
    for (const site of issueSites.value) {
      const companyName = site.icpUnitName || site.title || site.domain;
      const detail = `检测发现 ${site.siteIssues!.length} 个问题：\n` + site.siteIssues!.map(i => `• ${i}`).join('\n') +
        `\n\nHTTPS:${site.siteHttps ? '支持' : '不支持'} | 移动端:${site.siteViewport ? '适配' : '不适配'} | 响应:${site.siteMs}ms`;
      try {
        await apiPost('/api/v1/crm/signals', {
          leadId: null,
          companyRefId: null,
          companyName,
          type: 0, // WebsiteAbnormal
          title: `网站检测异常：${site.domain}`,
          detail,
          source: '网站自动检测',
          sourceUrl: `http://${site.domain}`,
          eventAt: new Date().toISOString(),
          confidence: 90,
          evidenceType: '网站检测',
          queryParams: `domain=${site.domain}`,
          rawContent: JSON.stringify({ https: site.siteHttps, viewport: site.siteViewport, issues: site.siteIssues, responseMs: site.siteMs })
        });
        created++;
      } catch { /* 单条失败不中断 */ }
    }
    ElMessage.success(`已创建 ${created} 条网站异常信号`);
  } catch (e: any) {
    ElMessage.error('创建信号失败');
  } finally {
    signaling.value = false;
  }
}

function toAssetInput(row: QuakeSite) {
  if (row.assetInput) return row.assetInput;
  return {
    domain: row.domain || null,
    hostname: row.domain || null,
    ipAddress: row.ip || null,
    port: row.port ?? null,
    title: row.title || null,
    serviceName: row.service || null,
    cityCn: row.city || null,
    provinceCn: row.province || null,
    osName: row.os || null,
    // This is a search summary, not Quake's original JSON or a verified company record.
    rawJson: JSON.stringify({ source: 'quake_search_summary', query: query.value,
      domain: row.domain, ip: row.ip, port: row.port, title: row.title,
      service: row.service, city: row.city, province: row.province })
  };
}

async function openAssetPreview(items: QuakeSite[]) {
  previewBatchId.value = crypto.randomUUID();
  previewPage.value = 1;
  previewRows.value = items.map(item => ({ ...item, duplicateInBatch: false }));
  previewVisible.value = true;
  previewLoading.value = true;
  try {
    const seen = new Set<string>();
    const checked: PreviewRow[] = [];
    for (let offset = 0; offset < items.length; offset += 500) {
      const chunk = items.slice(offset, offset + 500);
      const preview = await apiPost<{ items: { duplicateInRequest: boolean; existingAssetId?: string | number | null; cleanVerdict: string }[] }>(
        '/api/v1/crm/quake/assets/preview', chunk.map(toAssetInput)
      );
      chunk.forEach((row, index) => {
        const key = `${row.domain.trim().toLowerCase().replace(/\.$/, '')}:${row.port ?? ''}`;
        const duplicate = seen.has(key) || (preview.items[index]?.duplicateInRequest ?? false);
        seen.add(key);
        checked.push({ ...row, duplicateInBatch: duplicate,
          existingAssetId: preview.items[index]?.existingAssetId,
          cleanVerdict: preview.items[index]?.cleanVerdict });
      });
    }
    previewRows.value = checked;
  } catch (e: any) {
    previewVisible.value = false;
    ElMessage.error(e?.detail || '资产预检失败，未保存任何数据');
  } finally {
    previewLoading.value = false;
  }
}

async function saveAssetCandidates() {
  if (savingAssets.value || previewLoading.value || pendingRows.value.length === 0) return;
  savingAssets.value = true;
  let stored = 0, skipped = 0, failed = 0;
  try {
    const items = pendingRows.value.map(toAssetInput);
    const errors: string[] = [];
    for (let offset = 0; offset < items.length; offset += 500) {
      const res = await apiPost<{ stored: number; skipped: number; failed: number; errors: string[] }>(
        `/api/v1/crm/quake/assets/import?batchId=${previewBatchId.value}`, items.slice(offset, offset + 500)
      );
      stored += res.stored;
      skipped += res.skipped;
      failed += res.failed;
      errors.push(...(res.errors || []));
    }
    previewVisible.value = false;
    const summary = `候选资产留存：新增 ${stored} 条，已存在 ${previewExistingCount.value + skipped} 条，失败 ${failed} 条；未创建线索。`;
    if (failed) ElMessage.warning(`${summary} ${errors[0] || ''}`);
    else ElMessage.success(summary);
  } catch (e: any) {
    ElMessage.error(`留存中断：已新增 ${stored} 条、跳过 ${skipped} 条、行失败 ${failed} 条。重新预检后可安全继续。${e?.detail || ''}`);
  } finally {
    savingAssets.value = false;
  }
}
</script>

<style scoped>
.card-header { display: flex; justify-content: space-between; align-items: center; }
.hint { color: #909399; font-size: 12px; font-weight: normal; }
.search-bar { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; margin-bottom: 16px; }
.file-import { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin: 12px 0; padding: 12px 14px; border: 1px solid #e4e7ed; border-radius: 6px; }
.quake-configuration { margin-bottom: 18px; padding: 12px 14px; border: 1px solid #e4e7ed; border-radius: 6px; }
.configuration-status, .configuration-form { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.configuration-form { margin-top: 12px; }
.configuration-form .el-input { max-width: 420px; }
.quake-configuration .el-alert { margin-top: 10px; }
.quake-configuration p { margin: 10px 0 0; }
.results-toolbar { display: flex; justify-content: space-between; align-items: center; margin-top: 16px; }
</style>
