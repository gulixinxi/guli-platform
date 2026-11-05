import JSZip from 'jszip';
import * as XLSX from 'xlsx';

export interface QuakeExportAsset {
  domain: string | null;
  hostname: string | null;
  ipAddress: string | null;
  url: string | null;
  port: number | null;
  sitePath: string | null;
  transport: string | null;
  asn: number | null;
  asnOrganization: string | null;
  serviceName: string | null;
  serviceVersion: string | null;
  countryCn: string | null;
  provinceCn: string | null;
  cityCn: string | null;
  title: string | null;
  serverHeader: string | null;
  rawJson: string;
}

const MAX_FILE_BYTES = 20 * 1024 * 1024;
const MAX_JSON_BYTES = 30 * 1024 * 1024;
const MAX_ROWS = 10000;
const str = (value: unknown): string | null =>
  typeof value === 'string' && value.trim() ? value.trim() : null;
const number = (value: unknown): number | null => {
  if (value === null || value === undefined || value === '') return null;
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : null;
};
const record = (value: unknown): Record<string, unknown> =>
  value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};

function fromQuakeRow(value: unknown): QuakeExportAsset {
  const row = record(value);
  const service = record(row.service);
  const http = record(service.http);
  const location = record(row.location);
  const rawUrl = str(row.url);
  let urlHost: string | null = null;
  try { if (rawUrl) urlHost = new URL(rawUrl).hostname; } catch { /* invalid URL remains visible in raw evidence */ }
  const host = str(http.host) ?? str(row.hostname) ?? str(row.domain) ?? urlHost ?? str(row.ip);
  const isIp = !!host && /^\d{1,3}(?:\.\d{1,3}){3}$/.test(host);
  const domain = str(row.domain) ?? (isIp ? null : host);
  const path = str(http.path) ?? '/';
  const scheme = str(service.name)?.includes('ssl') ? 'https' : 'http';
  return {
    domain, hostname: host, ipAddress: str(row.ip),
    url: rawUrl ?? (host ? `${scheme}://${host}${path}` : null),
    port: number(row.port), sitePath: path, transport: str(row.transport),
    asn: number(row.asn), asnOrganization: str(row.org),
    serviceName: str(service.name), serviceVersion: str(service.version),
    countryCn: str(location.country_cn), provinceCn: str(location.province_cn), cityCn: str(location.city_cn),
    title: str(http.title), serverHeader: str(http.server), rawJson: JSON.stringify(value)
  };
}

function fromExcelRow(value: unknown): QuakeExportAsset {
  const row = record(value);
  const get = (...names: string[]) => names.map(name => str(row[name])).find(Boolean) ?? null;
  const host = get('网站Host（域名）', '网站Host', 'hostname', 'host', '国际化域名', 'domain');
  const rawUrl = get('URL', 'url');
  let urlHost: string | null = null;
  try { if (rawUrl) urlHost = new URL(rawUrl).hostname; } catch { /* retain raw value */ }
  const target = host ?? urlHost;
  const isIp = !!target && /^\d{1,3}(?:\.\d{1,3}){3}$/.test(target);
  return {
    domain: isIp ? null : target, hostname: target, ipAddress: get('IP地址', 'ip', 'ipaddress'),
    url: rawUrl, port: number(get('端口号', 'port')), sitePath: get('网站路径', 'path'),
    transport: get('传输层协议', 'transport'), asn: number(get('自治域号码', 'asn')),
    asnOrganization: get('自治域归属组织', 'org'), serviceName: get('服务名称', 'service'),
    serviceVersion: get('服务版本号', 'version'), countryCn: get('国家（中文）', 'country_cn'),
    provinceCn: get('省份（中文）', 'province_cn'), cityCn: get('城市（中文）', 'city_cn'),
    title: get('网站标题', 'title', '_title'), serverHeader: get('_server', 'server'),
    rawJson: JSON.stringify(value)
  };
}

export async function readQuakeExport(file: File): Promise<QuakeExportAsset[]> {
  if (file.size > MAX_FILE_BYTES) throw new Error('文件超过 20 MB，请分批导出。');
  const bytes = await file.arrayBuffer();
  const digest = await crypto.subtle.digest('SHA-256', bytes);
  const fileSha256 = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
  const withEvidence = (assets: QuakeExportAsset[]) => assets.map((asset, index) => ({
    ...asset,
    rawJson: JSON.stringify({ source: 'quake_export_file', fileName: file.name,
      fileSha256, rowNumber: index + 1, row: JSON.parse(asset.rawJson) })
  }));
  const name = file.name.toLowerCase();
  let rows: unknown;
  if (name.endsWith('.zip')) {
    const zip = await JSZip.loadAsync(bytes);
    const entries = Object.values(zip.files).filter(entry => !entry.dir && entry.name.toLowerCase().endsWith('.json'));
    if (entries.length !== 1 || entries[0].name.includes('..') || entries[0].name.startsWith('/'))
      throw new Error('ZIP 必须只包含一个 Quake JSON 文件。');
    const json = await entries[0].async('string');
    if (new TextEncoder().encode(json).length > MAX_JSON_BYTES) throw new Error('解压后的 JSON 超过 30 MB。');
    rows = JSON.parse(json);
  } else if (name.endsWith('.json')) {
    rows = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } else if (name.endsWith('.xlsx') || name.endsWith('.xls')) {
    const book = XLSX.read(bytes, { type: 'array' });
    if (!book.SheetNames.length) throw new Error('Excel 中没有工作表。');
    const excelRows = XLSX.utils.sheet_to_json(book.Sheets[book.SheetNames[0]], { defval: null });
    if (excelRows.length < 1 || excelRows.length > MAX_ROWS) throw new Error('Excel 记录数需在 1–10000 条之间。');
    return withEvidence(excelRows.map(fromExcelRow));
  } else {
    throw new Error('请选择 Quake JSON、单 JSON 的 ZIP 或 Excel 文件。');
  }
  const list = Array.isArray(rows) ? rows : record(rows).data;
  if (!Array.isArray(list) || list.length < 1 || list.length > MAX_ROWS)
    throw new Error('JSON 必须是 1–10000 条记录的数组。');
  return withEvidence(list.map(fromQuakeRow));
}
