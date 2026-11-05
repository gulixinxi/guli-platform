// ASP.NET serializes CRM enums as names; older responses can still contain numbers.
export function crmEnumNumber(value: unknown, values: Record<string, string | number>): number {
  if (typeof value === 'number') return value;
  if (typeof value === 'string') {
    const named = values[value];
    if (typeof named === 'number') return named;
    const numeric = Number(value);
    if (value.trim() && Number.isFinite(numeric)) return numeric;
  }
  return Number.NaN;
}

// ---------------------------------------------------------------------------
// Shared label dictionaries
// ---------------------------------------------------------------------------
//
// WHY THIS FILE EXISTS (阶段 A：修复信号/商机状态枚举显示)
//
// The CRM views used to carry their own inline label arrays. That produced
// two real defects, not just style drift:
//
//   1. CONTRADICTION — BusinessCategory.DouyinAddress (6) rendered as
//      "抖音地址" in DealList.vue but "短视频地址" in CompanyDetail.vue.
//      The same deal showed two different service names depending on which
//      page the user opened.
//   2. TRUNCATION — CompanyDetail.vue's array stopped at index 7, so
//      SocialMedia (8) and ForeignTradeLeads (9) silently fell through to
//      the "其他" fallback. The user saw "其他" for a self-media or
//      foreign-trade deal.
//
// Root cause: positional arrays are keyed by INDEX, so adding a value in the
// middle of an enum silently re-labels every later value, and a short array
// silently truncates. Every dictionary below is keyed by the SYMBOLIC enum
// member instead, so a missing entry is a compile error rather than a
// wrong label in production.
//
// Any label change goes here, once. Views import from this file.

import {
  BusinessCategory,
  CompanyCity,
  CompanyIndustry,
  CompanyScale,
  CustomerStatus,
  DealStage,
  DealStatus,
  FollowType,
  IntentRating,
  LeadStatus,
  LogStatus,
  LostReason,
  OperationType,
  SubjectType,
} from '../types/crm';

/** A label dictionary keyed by enum member, never by array position. */
export type LabelMap<T> = Record<number, string>;

export const businessCategoryLabels: LabelMap<BusinessCategory> = {
  [BusinessCategory.WebsiteBuild]: '企业建站',
  [BusinessCategory.WebsiteOpt]: '网站优化',
  [BusinessCategory.AIApp]: 'AI应用',
  [BusinessCategory.GEOOpt]: 'GEO优化',
  [BusinessCategory.DigitalEmployee]: '数字员工',
  [BusinessCategory.PoiAnnotation]: '地图标注',
  // 后端 CrmEnums.cs: "抖音/快手地址添加" — the 抖音 wording is the
  // canonical one. CompanyDetail.vue used to say "短视频地址" here.
  [BusinessCategory.DouyinAddress]: '抖音地址',
  [BusinessCategory.StoreInfoComplete]: '门店信息完善',
  [BusinessCategory.SocialMedia]: '自媒体运营',
  [BusinessCategory.ForeignTradeLeads]: '外贸获客',
  [BusinessCategory.Other]: '其他',
};

export const dealStageLabels: LabelMap<DealStage> = {
  [DealStage.InitialContact]: '初步接触',
  [DealStage.Requirement]: '需求确认',
  [DealStage.Proposal]: '方案报价',
  [DealStage.Negotiation]: '商务谈判',
  [DealStage.Won]: '成交',
  [DealStage.Lost]: '丢单',
};

export const dealStatusLabels: LabelMap<DealStatus> = {
  [DealStatus.InProgress]: '进行中',
  [DealStatus.Won]: '已成交',
  [DealStatus.Lost]: '已丢单',
  [DealStatus.Paused]: '暂搁',
};

export const lostReasonLabels: LabelMap<LostReason> = {
  [LostReason.Price]: '价格',
  [LostReason.Competitor]: '竞品',
  [LostReason.NoBudget]: '无预算',
  [LostReason.BadTiming]: '时机不对',
  [LostReason.NoContact]: '联系不上',
  [LostReason.Other]: '其他',
};

export const leadStatusLabels: LabelMap<LeadStatus> = {
  [LeadStatus.Pending]: '待认领',
  [LeadStatus.Claimed]: '已认领',
  [LeadStatus.Converted]: '已转化',
  [LeadStatus.Discarded]: '已废弃',
};

export const customerStatusLabels: LabelMap<CustomerStatus> = {
  [CustomerStatus.ToContact]: '待联系',
  [CustomerStatus.Following]: '跟进中',
  [CustomerStatus.Quoted]: '已报价',
  [CustomerStatus.Deal]: '成交',
  [CustomerStatus.Paused]: '暂搁',
  [CustomerStatus.Invalid]: '无效',
};

export const companyIndustryLabels: LabelMap<CompanyIndustry> = {
  [CompanyIndustry.Machinery]: '机械制造',
  [CompanyIndustry.Textile]: '纺织服装',
  [CompanyIndustry.Food]: '食品加工',
  [CompanyIndustry.Chemical]: '化工',
  [CompanyIndustry.BuildingMaterials]: '建材',
  [CompanyIndustry.Agriculture]: '农业种植',
  [CompanyIndustry.Trading]: '商贸流通',
  [CompanyIndustry.Service]: '服务业',
  [CompanyIndustry.Other]: '其他',
};

export const companyScaleLabels: LabelMap<CompanyScale> = {
  [CompanyScale.Small]: '≤20人',
  [CompanyScale.Medium]: '21-100人',
  [CompanyScale.Large]: '101-300人',
  [CompanyScale.XLarge]: '301-1000人',
  [CompanyScale.XXLarge]: '>1000人',
  [CompanyScale.Unknown]: '未知',
};

export const intentRatingLabels: LabelMap<IntentRating> = {
  [IntentRating.A]: 'A',
  [IntentRating.B]: 'B',
  [IntentRating.C]: 'C',
  [IntentRating.D]: 'D',
};

// Wording adopted from LeadList.vue's shipped label set — these are the terms
// the sales team already reads, so they are the canonical ones here.
export const subjectTypeLabels: LabelMap<SubjectType> = {
  [SubjectType.Enterprise]: '企业',
  [SubjectType.Association]: '协会/商会',
  [SubjectType.ServiceOrg]: '服务中心',
  [SubjectType.ProfessionalService]: '专业服务',
  [SubjectType.Park]: '园区/基地',
  [SubjectType.Government]: '政府机关',
  [SubjectType.PublicInstitution]: '事业单位',
  [SubjectType.Education]: '学历教育',
  [SubjectType.Training]: '教育培训',
  [SubjectType.IndividualBusiness]: '个体商户/门店',
  [SubjectType.Other]: '其他',
};

export const logStatusLabels: LabelMap<LogStatus> = {
  [LogStatus.Success]: '成功',
  [LogStatus.PartialFailed]: '部分失败',
  [LogStatus.Failed]: '失败',
};

// --- Demand assessment (线索需求评估) -------------------------------------
// These are STRING enums on the wire (LeadDemandAssessment.Category is a
// string column, not the numeric BusinessCategory), so they get their own
// string-keyed dictionaries.

export const assessmentCategoryLabels: Record<string, string> = {
  website: '建站',
  geo: 'GEO',
  social_media: '自媒体',
  foreign_trade: '外贸获客',
};

/**
 * Assessment category (string wire value) → CRM BusinessCategory.
 *
 * This is the ONE place on the frontend that knows how a demand-assessment
 * category maps onto the numeric business category a Deal is created with.
 * It used to be an inline `{ website: 0, geo: 3, ... }` literal in
 * LeadList.vue written with bare numbers, which meant inserting a
 * BusinessCategory value could silently re-point the mapping.
 *
 * The server enforces the same mapping (CrmEndpoints assessment→deal
 * check); if you change one side, change the other.
 */
const assessmentToBusinessCategory: Record<string, BusinessCategory> = {
  website: BusinessCategory.WebsiteBuild,
  geo: BusinessCategory.GEOOpt,
  social_media: BusinessCategory.SocialMedia,
  foreign_trade: BusinessCategory.ForeignTradeLeads,
};

/** Returns null when the assessment category has no business-category peer. */
export function businessCategoryForAssessment(category: unknown): number | null {
  if (typeof category !== 'string') return null;
  const v = assessmentToBusinessCategory[category];
  return v === undefined ? null : v;
}

export const assessmentStateLabels: Record<string, string> = {
  unknown: '未知',
  pending: '待核验',
  potential: '可能有需求',
  confirmed: '已确认需求',
  not_applicable: '不适用',
};

// --- Demand-assessment evidence (阶段 B1) -------------------------------
// Mirrors LeadEvidenceTypes / LeadEvidenceVerifications in
// modules/crm/GuliERP.Crm.Domain/Entities/LeadEvidenceVocabulary.cs.

export const evidenceTypeLabels: Record<string, string> = {
  website: '官网检测',
  icp: 'ICP 备案',
  geo: 'GEO 诊断',
  signal: '情报信号',
  quake: 'Quake 资产',
  reverse_lookup: '单位反查',
  manual: '人工录入',
  legacy_text: '历史记录（未结构化）',
};

export const evidenceVerificationLabels: Record<string, string> = {
  not_verified: '未核验',
  verified: '已核验成立',
  refuted: '已核验推翻',
  not_found: '查询未命中',
  confirmed_absent: '确认不存在',
  outdated: '来源已过期',
};

/** Ordered option lists for the evidence editor's selects. */
export const evidenceTypeOptions = Object.entries(evidenceTypeLabels)
  .filter(([k]) => k !== 'legacy_text')      // migration artefact, never selectable
  .map(([value, label]) => ({ value, label }));

export const evidenceVerificationOptions = Object.entries(evidenceVerificationLabels)
  .map(([value, label]) => ({ value, label }));

// --- Signal / verification (情报信号) -----------------------------------
// These three backend enums (SignalType, SignalStatus,
// VerificationStatus in modules/crm/GuliERP.Crm.Domain/Enums/
// CompanyEnums.cs) have NO TypeScript counterpart in types/crm.ts —
// SignalCenter.vue carried the name maps locally and CompanyDetail.vue
// carried a second, TRUNCATED copy of the label list. This is their
// single home now.
//
// VerificationStatus is the PRD's「未知与否定分离」carrier: NotFound
// ("未查到") and ConfirmedAbsent ("已确认不存在") are distinct states
// and must never collapse into one another.

/** SignalType, name → value (for crmEnumNumber). */
export const signalTypeValues: Record<string, number> = {
  WebsiteAbnormal: 0, WebsiteMissing: 1, GeoChange: 2, IcpChange: 3,
  LicenseExpiring: 4, Penalty: 5, Bidding: 6, Policy: 7, Hiring: 8,
  ForeignTrade: 9, PoiMissing: 10, SuspectedDelisted: 11,
  AnnouncementAlert: 12, Other: 99,
};

export const signalTypeLabels: LabelMap<never> = {
  0: '网站异常', 1: '官网缺失', 2: 'GEO变化', 3: 'ICP变动',
  4: '许可证到期', 5: '政府处罚', 6: '招标采购', 7: '政策补贴',
  8: '招聘扩张', 9: '外贸信息', 10: '地图信息缺失', 11: '疑似下架',
  12: '整顿公告', 99: '其他',
};

/** SignalStatus, name → value. */
export const signalStatusValues: Record<string, number> = {
  Pending: 0, Confirmed: 1, Ignored: 2, Converted: 3,
};

export const signalStatusLabels: LabelMap<never> = {
  0: '待处理', 1: '已确认', 2: '已忽略', 3: '已转商机',
};

/** VerificationStatus, name → value. */
export const verificationStatusValues: Record<string, number> = {
  NotQueried: 0, ConfirmedExist: 1, ConfirmedAbsent: 2,
  NotFound: 3, Outdated: 4, NeedManual: 5,
};

export const verificationStatusLabels: LabelMap<never> = {
  0: '未查询', 1: '已确认存在', 2: '已确认不存在',
  3: '未查到', 4: '数据过期', 5: '需人工核实',
};

// --- Cities / follow-up / import-export ---------------------------------

export const companyCityLabels: LabelMap<CompanyCity> = {
  [CompanyCity.Weifang]: '潍坊',
  [CompanyCity.Qingdao]: '青岛',
  [CompanyCity.Yantai]: '烟台',
  [CompanyCity.Jinan]: '济南',
  [CompanyCity.Zibo]: '淄博',
  [CompanyCity.Linyi]: '临沂',
  [CompanyCity.ShandongOther]: '山东其他',
  [CompanyCity.OutOfProvince]: '省外',
};

export const followTypeLabels: LabelMap<FollowType> = {
  [FollowType.Phone]: '电话',
  [FollowType.Wechat]: '微信',
  [FollowType.Visit]: '拜访',
  [FollowType.Email]: '邮件',
  [FollowType.Meeting]: '会议',
  [FollowType.Other]: '其他',
};

export const operationTypeLabels: LabelMap<OperationType> = {
  [OperationType.Import]: '导入',
  [OperationType.Export]: '导出',
  [OperationType.BatchDelete]: '批量删除',
  [OperationType.BatchReassign]: '批量改派',
};

/**
 * Resolve a numeric wire value to a Chinese label from a symbol-keyed
 * dictionary. Returns `fallback` for unknown values; never throws on NaN.
 *
 * Accepts an enum NAME string too ("WebsiteBuild"), because ASP.NET may
 * serialize enums either way across API versions.
 */
export function crmLabel(
  dict: LabelMap<never>,
  value: unknown,
  enumValues?: Record<string, string | number>,
  fallback = '未知'
): string {
  const n = typeof value === 'number' ? value
    : typeof value === 'string'
      ? (enumValues ? crmEnumNumber(value, enumValues) : Number(value))
      : Number.NaN;
  if (!Number.isFinite(n)) return fallback;
  return dict[n] ?? fallback;
}

/** Resolve a string-enum wire value (e.g. "pending") to a Chinese label. */
export function crmTextLabel(
  dict: Record<string, string>,
  value: unknown,
  fallback = '未知'
): string {
  return typeof value === 'string' ? dict[value] ?? fallback : fallback;
}

/** Business-category label; tolerant of number or enum-name on the wire. */
export function businessCategoryLabel(value: unknown, fallback = '其他'): string {
  return crmLabel(businessCategoryLabels, value, BusinessCategory, fallback);
}