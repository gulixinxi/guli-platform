<template>
  <el-dialog v-model="open" title="批量录入第三方身份候选" width="min(900px, 96vw)">
    <el-alert title="先预检，确认后才保存待审候选。只接收已有结果文件，不会访问第三方网站；不会确认身份、创建线索或客户。" type="info" :closable="false" />
    <p class="hint">支持 CSV / Excel，最多 200 行。列：域名、公司名、来源网址、证据原文、采集时间。站长之家结果表可用“证据来源”列识别，页面链接按域名生成；请指定实际采集日期。</p>
    <div class="controls">
      <input type="file" accept=".csv,.xlsx,.xls" @change="onFile" />
      <el-date-picker v-model="batchObservedAt" type="datetime" placeholder="文件实际采集时间" />
      <el-button :disabled="!fileRows.length || !batchObservedAt" :loading="busy" @click="preview">预检</el-button>
    </div>
    <div v-if="fileRows.length" class="hint">已读取 {{ fileRows.length }} 行；预检不会写入数据库。</div>
    <el-table v-if="result" :data="result.items" max-height="350" stripe>
      <el-table-column prop="row" label="行" width="60" />
      <el-table-column prop="domain" label="域名" min-width="160" />
      <el-table-column prop="status" label="状态" width="155"><template #default="{ row }">{{ statusLabel(row.status) }}</template></el-table-column>
      <el-table-column prop="reason" label="说明" min-width="250" />
    </el-table>
    <template #footer>
      <span v-if="result" class="hint">共 {{ result.total }} 行，可保存 {{ result.ready }} 行；其余保持待处理。</span>
      <el-button @click="open = false">关闭</el-button>
      <el-button type="primary" :disabled="!result?.ready || !previewedRows || busy" :loading="busy" @click="commit">保存可用记录为待审候选</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { apiPost } from '../../api/http';
import { readSpreadsheetRows } from '../../utils/spreadsheet';

interface Row { domain: string; proposedName: string; sourceReference: string; claimText: string; observedAt: string }
interface ResultRow { row: number; domain: string; status: string; reason: string; assetId?: string }
interface Result { total: number; ready: number; created: number; items: ResultRow[] }
const open = defineModel<boolean>({ required: true });
const emit = defineEmits<{ imported: [] }>();
const fileRows = ref<Record<string, string>[]>([]);
const batchObservedAt = ref<Date | null>(null);
const result = ref<Result | null>(null);
const previewedRows = ref<Row[] | null>(null);
const busy = ref(false);
const fileName = ref('');
const fileSha256 = ref('');
const pick = (row: Record<string, string>, ...names: string[]) => names.map(name => (row[name] || '').trim()).find(Boolean) || '';
function statusLabel(status: string) { return ({ ready: '可保存', created: '已保存', invalid: '数据无效', no_asset: '无资产', ambiguous_asset: '多条资产', existing_candidate: '已有候选', duplicate_input: '批内重复' } as Record<string, string>)[status] || status; }
async function onFile(event: Event) {
  const input = event.target as HTMLInputElement;
  result.value = null; previewedRows.value = null; fileRows.value = []; fileName.value = ''; fileSha256.value = '';
  if (!input.files?.[0]) return;
  try {
    const rows = await readSpreadsheetRows(input.files[0]);
    if (rows.length > 200) throw new Error('每次最多 200 行，请拆分文件。');
    fileRows.value = rows;
    fileName.value = input.files[0].name;
    const digest = await crypto.subtle.digest('SHA-256', await input.files[0].arrayBuffer());
    fileSha256.value = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
  } catch (error: any) { ElMessage.error(error?.message || '文件读取失败'); }
}
function makeRows(): Row[] {
  if (!batchObservedAt.value) throw new Error('请填写文件实际采集时间。');
  return fileRows.value.map((row, index) => {
    const domain = pick(row, '域名', 'domain');
    const proposedName = pick(row, '公司名', '主体名称', 'proposedName');
    const reportedIcp = pick(row, 'ICP备案号', 'reportedIcpNumber');
    const chinaz = pick(row, '证据来源').includes('站长之家');
    const sourceReference = pick(row, '来源网址', 'sourceReference') || (chinaz && domain ? `https://seo.chinaz.com/${domain}` : '');
    const claimText = pick(row, '证据原文', 'claimText') || (chinaz && proposedName ? `导入文件声称站长之家页面显示主体：${proposedName}${reportedIcp ? `；备案号：${reportedIcp}` : ''}` : '');
    const rawObservedAt = pick(row, '采集时间', 'observedAt');
    const parsed = rawObservedAt ? new Date(rawObservedAt) : batchObservedAt.value!;
    if (Number.isNaN(parsed.getTime())) throw new Error(`第 ${index + 2} 行采集时间无效，请用 ISO 日期时间。`);
    const observedAt = parsed.toISOString();
    return { domain, proposedName, sourceReference, claimText, observedAt };
  });
}
async function preview() {
  busy.value = true; result.value = null; previewedRows.value = null;
  try {
    const rows = makeRows();
    const response = await apiPost<Result>('/api/v1/crm/identity-candidates/batch-preview', { items: rows, sourceFileName: fileName.value, sourceFileSha256: fileSha256.value });
    previewedRows.value = rows; result.value = response;
  } catch (error: any) { ElMessage.error(error?.detail || error?.message || '批量预检失败'); }
  finally { busy.value = false; }
}
async function commit() {
  if (!previewedRows.value || !result.value?.ready) return;
  try { await ElMessageBox.confirm(`将 ${result.value.ready} 行保存为待审身份候选。仅记录第三方线索，不确认企业身份。`, '确认批量保存', { type: 'warning' }); }
  catch { return; }
  busy.value = true;
  try {
    result.value = await apiPost<Result>('/api/v1/crm/identity-candidates/batch-import', { items: previewedRows.value, sourceFileName: fileName.value, sourceFileSha256: fileSha256.value });
    ElMessage.success(`已保存 ${result.value.created} 条待审候选。`);
    previewedRows.value = null; emit('imported');
  } catch (error: any) { ElMessage.error(error?.detail || '批量保存失败，请重新预检'); }
  finally { busy.value = false; }
}
</script>

<style scoped>
.controls { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin: 18px 0; }
.hint { color: var(--el-text-color-secondary); font-size: 12px; margin: 12px 0; }
</style>
