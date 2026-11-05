<template>
  <div class="identity-desk">
    <el-card shadow="never">
      <template #header>
        <div class="toolbar">
          <div><strong>企业身份待审资产</strong><div class="hint">Quake 观测记录只是资产线索，域名、IP、标题和备案主体均不能单独证明企业归属。</div></div>
          <div><el-button v-if="canManage" @click="batchDialog = true">批量录入候选</el-button><el-button v-if="canManage" :disabled="!chinazConfigured || !chosenAssets.length || chosenAssets.length > 10" :loading="chinazLoading" @click="lookupChinazBatch">批量查询站长之家</el-button><el-button :loading="loading" @click="loadAssets">刷新</el-button></div>
        </div>
      </template>
      <el-alert title="身份候选、证据及审核决定可保存。录入的来源真实性仍需人工核对；ICP备案查询结果不会自动保存。身份确认不会自动转成 CRM 线索。" type="warning" :closable="false" show-icon />
      <el-collapse class="recheck-panel" @change="loadSourceStatuses">
        <el-collapse-item name="sources" title="数据源能力与授权状态">
          <p class="hint">连接配置、开关和真实供应商授权是不同事项。此处只展示本系统状态，不会发起外部查询。</p>
          <el-table :data="sourceStatuses" v-loading="sourceLoading" stripe>
            <el-table-column prop="name" label="数据源" min-width="135" />
            <el-table-column label="当前状态" width="150"><template #default="{ row }">{{ sourceStatusLabel(row.status) }}</template></el-table-column>
            <el-table-column prop="availableFields" label="可作为候选的字段" min-width="190" />
            <el-table-column prop="authorization" label="授权核对" min-width="220" />
            <el-table-column prop="quota" label="额度/限速" min-width="210" />
            <el-table-column prop="cost" label="费用" width="100" />
            <el-table-column prop="retention" label="供应商保留期" width="125" />
            <el-table-column prop="note" label="使用边界" min-width="220" />
          </el-table>
          <el-button size="small" @click="loadSourceStatuses">刷新状态</el-button>
        </el-collapse-item>
      </el-collapse>
      <el-collapse class="recheck-panel" @change="loadRechecks">
        <el-collapse-item name="rechecks" title="网站定期复检管理（仅已确认身份的网站，默认关闭）">
          <div class="filters">
            <el-select v-model="recheckStageFilter" placeholder="全部 CRM 阶段" style="width:165px" @change="resetRecheckPage"><el-option label="全部 CRM 阶段" value="" /><el-option label="已转客户" value="customer" /><el-option label="已成线索" value="lead" /><el-option label="仅身份确认" value="identity" /></el-select>
            <span class="hint">7 天复检仅对单独开启的网站生效；每日最多排入 10 个自动任务。</span>
          </div>
          <div v-if="canConfigureChinaz" class="filters">
            <span>已选 {{ chosenRechecks.length }} 个</span>
            <el-button :disabled="!chosenRechecks.length" :loading="recheckSaving" type="primary" @click="saveRecheckBatch(true)">开启 7 天复检</el-button>
            <el-button :disabled="!chosenRechecks.length" :loading="recheckSaving" @click="saveRecheckBatch(false)">关闭复检</el-button>
          </div>
          <el-table :data="recheckRows" v-loading="recheckLoading" stripe @selection-change="chosenRechecks = $event">
            <el-table-column v-if="canConfigureChinaz" type="selection" width="46" />
            <el-table-column prop="proposedName" label="身份候选" min-width="190" />
            <el-table-column prop="domain" label="网站域名" min-width="180" />
            <el-table-column label="CRM 阶段" width="140"><template #default="{ row }">{{ recheckStageName(row) }}</template></el-table-column>
            <el-table-column label="复检" width="95"><template #default="{ row }"><el-tag :type="row.schedule.enabled ? 'success' : 'info'">{{ row.schedule.enabled ? '已开启' : '已关闭' }}</el-tag></template></el-table-column>
            <el-table-column label="下次复检" min-width="165"><template #default="{ row }">{{ formatTime(row.schedule.nextDueAt) }}</template></el-table-column>
            <el-table-column label="最近报告" min-width="170"><template #default="{ row }">{{ row.latestReportStatus ? `${reportStatusName(row.latestReportStatus)} · ${formatTime(row.latestReportAt)}` : '尚无报告' }}</template></el-table-column>
            <el-table-column label="设置记录" width="100"><template #default="{ row }"><el-button link type="primary" @click="showRecheckHistory(row)">查看</el-button></template></el-table-column>
          </el-table>
          <el-pagination v-if="recheckTotal > 50" v-model:current-page="recheckPage" :page-size="50" :total="recheckTotal" layout="prev, pager, next" @current-change="loadRechecks" />
          <p v-if="!recheckLoading && !recheckRows.length" class="hint">当前阶段没有已确认身份的网站。</p>
        </el-collapse-item>
      </el-collapse>
      <div v-if="canManage" class="filters">
        <strong>站长之家自动查询</strong>
        <el-tag :type="chinazConfigured ? 'success' : 'info'">后端当前{{ chinazConfigured ? '已开启' : '已关闭' }}</el-tag>
        <el-button size="small" @click="router.push('/crm/data-sources')">打开数据源设置</el-button>
      </div>
      <div class="filters">
        <el-input v-model="keyword" clearable placeholder="筛选域名、IP 或标题" style="max-width:300px" />
        <el-select v-model="verdict" style="width:150px"><el-option label="全部清洗结果" value="" /><el-option label="有效" value="valid" /><el-option label="可疑" value="suspect" /><el-option label="低质量" value="rejected" /></el-select>
        <el-button type="primary" :loading="loading" @click="searchAssets">搜索</el-button>
        <span class="hint">符合条件 {{ total }} 条；每页 {{ pageSize }} 条。</span>
      </div>
      <el-alert v-if="canManage && !chinazConfigured" title="站长之家自动查询当前关闭；仍可手工查看或导入已有结果文件。" type="info" :closable="false" />
      <el-table :data="assets" v-loading="loading" stripe @row-click="selectAsset" @selection-change="chosenAssets = $event" style="cursor:pointer">
        <el-table-column type="selection" width="46" />
        <el-table-column prop="domain" label="域名 / Host" min-width="190"><template #default="{ row }">{{ row.domain || row.hostname || '未提供' }}</template></el-table-column>
        <el-table-column prop="ipAddress" label="IP" min-width="120" />
        <el-table-column prop="title" label="标题（待核验）" min-width="200" show-overflow-tooltip><template #default="{ row }">{{ row.title || '未提供' }}</template></el-table-column>
        <el-table-column prop="cleanVerdict" label="清洗判定" width="110"><template #default="{ row }"><el-tag :type="row.cleanVerdict === 'valid' ? 'success' : row.cleanVerdict === 'rejected' ? 'danger' : 'warning'" size="small">{{ row.cleanVerdict }}</el-tag></template></el-table-column>
        <el-table-column prop="observedAt" label="观测时间" width="175"><template #default="{ row }">{{ formatTime(row.observedAt) }}</template></el-table-column>
        <el-table-column label="身份候选" width="110"><template #default><el-tag type="info" size="small">点击查看</el-tag></template></el-table-column>
      </el-table>
      <el-pagination v-if="total > pageSize" v-model:current-page="page" :page-size="pageSize" :total="total" layout="prev, pager, next" class="pagination" @current-change="loadAssets" />
      <el-empty v-if="!loading && !assets.length" description="当前筛选下暂无待审资产" />
    </el-card>
    <el-drawer v-model="drawer" title="资产与身份候选" size="min(680px, 100vw)" class="identity-drawer">
      <template v-if="selected">
        <el-alert v-if="focusedSignalId" type="info" :closable="false" class="signal-return"><template #title>已定位到网站提醒对应的报告与证据 <el-button size="small" link type="primary" @click="router.push({ path: '/crm/signals', query: { websiteSignalId: focusedSignalId } })">返回提醒并处理</el-button></template></el-alert>
        <el-descriptions :column="1" border>
          <el-descriptions-item label="域名 / Host">{{ selected.domain || selected.hostname || '未知' }}</el-descriptions-item>
          <el-descriptions-item label="IP / 端口">{{ selected.ipAddress || '未知' }}{{ selected.port ? `:${selected.port}` : '' }}</el-descriptions-item>
          <el-descriptions-item label="网站标题">{{ selected.title || '未提供' }}</el-descriptions-item>
          <el-descriptions-item label="来源 / 批次">{{ selected.server || 'Quake 候选' }} / {{ selected.batchId }}</el-descriptions-item>
          <el-descriptions-item label="清洗依据">{{ selected.cleanReasons || '未提供' }}</el-descriptions-item>
          <el-descriptions-item label="观测时间">{{ formatTime(selected.observedAt) }}</el-descriptions-item>
        </el-descriptions>
        <div class="evidence-head"><strong>ICP备案候选</strong><el-button size="small" :loading="icpLoading" :disabled="!lookupHost" @click="lookupIcp">查询 ICP</el-button></div>
        <el-alert v-if="!lookupHost" title="当前记录没有可查询的公开域名；IP 不作为企业身份。" type="info" :closable="false" />
        <el-alert v-if="icpError" :title="icpError" type="error" :closable="false" />
        <el-descriptions v-if="icp" :column="1" border>
          <el-descriptions-item label="查询状态">{{ icp.queryStatus === 'found' ? '查到备案候选' : icp.queryStatus === 'not_found' ? '未查到记录' : icp.queryStatus === 'not_configured' ? '服务器未配置查询工具' : '查询失败' }}</el-descriptions-item>
          <el-descriptions-item label="备案主体">{{ icp.unitName || '未知' }}</el-descriptions-item>
          <el-descriptions-item label="备案号">{{ icp.serviceLicence || icp.mainLicence || '未知' }}</el-descriptions-item>
          <el-descriptions-item label="来源 / 查询时间">beian CLI / {{ formatTime(icpCheckedAt) }}</el-descriptions-item>
        </el-descriptions>
        <p class="hint">备案主体可能是服务商；查询结果未保存，不能据此认定企业，也不生成信用代码。</p>
        <div class="evidence-head"><strong>第三方主体线索</strong><el-button size="small" :disabled="!lookupHost" @click="openChinaz">打开站长之家查询页</el-button></div>
        <p class="hint">人工查看后，按页面实际内容填写候选主体和证据原文；来源链接会预填。站长之家数据仅作候选，不能单独确认身份。系统不自动抓取该页面。</p>
        <div class="evidence-head"><strong>身份候选与审核记录</strong><el-button size="small" :loading="candidateLoading" @click="loadCandidates">刷新候选</el-button></div>
        <el-alert v-if="candidateError" :title="candidateError" type="error" :closable="false" />
        <el-empty v-if="!candidateLoading && !candidates.length" description="尚无企业身份候选" />
        <el-card v-for="candidate in candidates" :key="candidate.id" class="candidate-card" shadow="never">
          <div class="toolbar"><strong>{{ candidate.proposedName }}</strong><el-tag :type="candidate.status === 'confirmed' ? 'success' : candidate.status === 'rejected' ? 'danger' : 'info'">{{ statusText(candidate.status) }}</el-tag></div>
          <div class="hint">版本 {{ candidate.version }} · 提议人 {{ candidate.createdBy }} · {{ formatTime(candidate.createdAt) }}</div>
          <ul class="evidence-list"><li v-for="item in candidate.evidence" :key="item.id"><strong>{{ sourceText(item.sourceType) }}</strong>：{{ item.claimText }}<div class="hint">{{ item.sourceReference }} · 采集 {{ formatTime(item.observedAt) }} · 记录 {{ formatTime(item.recordedAt) }}</div></li></ul>
          <div v-if="candidate.reviewEvents.length" class="hint">审核历史：<span v-for="event in candidate.reviewEvents" :key="event.id">{{ statusText(event.action) }}（{{ event.actorId }}，{{ event.reason }}，{{ formatTime(event.occurredAt) }}）；</span></div>
          <div class="candidate-actions"><el-button v-if="canManage && candidate.status === 'pending'" size="small" @click="openEvidence(candidate)">追加证据</el-button><el-button v-if="canReview && candidate.status === 'pending'" size="small" type="success" @click="review(candidate, 'confirm')">确认身份</el-button><el-button v-if="canReview && candidate.status === 'pending'" size="small" type="danger" @click="review(candidate, 'reject')">驳回</el-button><el-button v-if="canReview && candidate.status !== 'pending'" size="small" @click="review(candidate, 'reopen')">撤销审核后补证</el-button><el-button v-if="canManage && candidate.status === 'confirmed'" size="small" @click="requestReport(candidate)">申请网站诊断</el-button><el-button v-if="candidate.status === 'confirmed'" size="small" type="primary" @click="loadPreview(candidate)">转线索预检</el-button></div>
          <div v-if="candidate.status === 'confirmed'" class="candidate-actions"><el-button size="small" link @click="loadReports(candidate)">查看诊断历史</el-button></div>
          <AiPreparationPanel v-if="candidate.status === 'confirmed'" :candidate-id="candidate.id" :can-manage="canManage" :can-review="canReview" />
          <div v-if="candidate.status === 'confirmed' && canManageContacts" class="candidate-card">
            <div class="evidence-head"><strong>联系人候选</strong><div><el-button size="small" @click="loadContactCandidates(candidate)">刷新</el-button><el-button v-if="canManageContacts" size="small" type="primary" @click="openContactForm(candidate)">录入公开联系人线索</el-button></div></div>
            <p class="hint">有联系人管理权限的账号可直接核对完整号码，查看行为会留痕；待审信息未经确认，招聘电话不能直接转销售联系人。</p>
            <el-table v-if="contactCandidates[candidate.id]?.length" :data="contactCandidates[candidate.id]" stripe>
              <el-table-column prop="name" label="姓名" width="110" />
              <el-table-column prop="position" label="职务" width="110" />
              <el-table-column label="邮箱" min-width="220"><template #default="{ row }"><ContactActionValue :value="row.email" kind="email" label="邮箱" /></template></el-table-column>
              <el-table-column label="电话" min-width="190"><template #default="{ row }"><ContactActionValue :value="row.phone" kind="phone" label="电话" /></template></el-table-column>
              <el-table-column prop="sourceType" label="来源类型" width="110" />
              <el-table-column prop="status" label="状态" width="100" />
              <el-table-column label="保留到期" min-width="150"><template #default="{ row }">{{ formatTime(row.expiresAt) }}</template></el-table-column>
              <el-table-column label="操作" min-width="260"><template #default="{ row }">
                <el-button v-if="canManageContacts" link @click="showContactEvents(row)">操作记录</el-button>
                <el-button v-if="canManageContacts && row.status !== 'redacted' && row.status !== 'expired'" link @click="revealContact(row)">查看来源详情</el-button>
                <el-button v-if="canManageContacts && row.status === 'pending'" link type="success" @click="reviewContact(candidate, row, 'confirm')">确认</el-button>
                <el-button v-if="canManageContacts && row.status === 'pending'" link type="danger" @click="reviewContact(candidate, row, 'reject')">驳回</el-button>
                <el-button v-if="canManageContacts && row.status === 'confirmed'" link @click="promoteContact(candidate, row)">转正式联系人</el-button>
                <el-button v-if="canManageContacts && row.status !== 'redacted' && row.status !== 'expired'" link @click="openContactForm(candidate, row)">更正</el-button>
                <el-button v-if="canManageContacts && row.status !== 'redacted'" link type="danger" @click="redactContact(candidate, row)">删除候选信息</el-button>
              </template></el-table-column>
            </el-table>
          </div>
          <el-alert v-if="previews[candidate.id]" :title="previews[candidate.id].ready ? '转线索预检通过' : '转线索预检未通过'" :type="previews[candidate.id].ready ? 'success' : 'warning'" :closable="false" class="candidate-card">
            <template #default><div v-for="reason in previews[candidate.id].blockers" :key="reason">{{ reason }}</div><div>{{ previews[candidate.id].note }}</div><el-button v-if="previews[candidate.id].ready && canManage && canReview" size="small" type="primary" @click="promoteLead(candidate)">确认转为线索</el-button><el-button v-if="previews[candidate.id].existingLead" size="small" @click="goToLeadAssessment(candidate.proposedName)">到线索池填写需求评估</el-button></template>
          </el-alert>
          <div v-if="reports[candidate.id]?.length" class="candidate-card"><strong>网站与 GEO 技术报告</strong><div class="hint">只检查公开网页技术项，不代表实际 AI 推荐率。</div><el-card v-for="report in reports[candidate.id]" :id="`website-report-${report.id}`" :key="report.id" shadow="never" class="candidate-card" :class="{ 'signal-focus': focusedReportId === report.id }"><div>版本 {{ report.version }} · {{ report.status }} · {{ formatTime(report.requestedAt) }} · 尝试 {{ report.attemptCount }} 次</div><div v-if="report.error" class="hint">{{ report.error }}</div><div v-if="report.attemptsDataWarning" class="hint">{{ report.attemptsDataWarning }}</div><div v-if="report.comparedWithVersion" class="report-diff"><strong>与版本 {{ report.comparedWithVersion }} 对比</strong><div v-if="!report.changes.length" class="hint">技术项状态未变化</div><div v-for="change in report.changes" :key="change.key">{{ change.key }}：{{ change.previousStatus }} → {{ change.currentStatus }}</div></div><div v-for="check in report.checks" :key="check.key" class="hint">{{ check.key }}：{{ check.status }} · {{ check.evidence }}</div><el-button v-if="canManage && report.status === 'completed'" size="small" @click="openContentEvidence(candidate, report)">录入内容观察</el-button></el-card></div>
          <el-alert v-if="contentReviewDue[candidate.id]" class="candidate-card" type="warning" :closable="false" title="定期复检完成，内容证据待人工核对"><template #default>技术报告不能判断页面内容是否变化。请核对当前网页并录入新证据。<el-button v-if="canManage" size="small" @click="openDueContentEvidence(candidate)">录入本次观察</el-button><el-button size="small" link @click="router.push('/crm/signals')">查看情报提醒</el-button></template></el-alert>
          <div v-if="contentEvidence[candidate.id]?.length" class="candidate-card"><strong>内容观察与证据审核</strong><p class="hint">人工记录的公开页面观察；未调用 AI 平台，不能据此宣称被推荐。</p><el-card v-for="row in contentEvidence[candidate.id]" :id="`website-evidence-${row.id}`" :key="row.id" shadow="never" class="candidate-card" :class="{ 'signal-focus': focusedEvidenceId === row.id }"><div>报告 {{ reportVersion(candidate.id, row.reportId) }} · {{ row.status === 'pending' ? '待审核' : row.status === 'accepted' ? '已审核通过' : '已驳回' }} · {{ formatTime(row.observedAt) }}</div><div>来源：<a v-if="safeExternalHttpUrl(row.sourceUrl)" :href="safeExternalHttpUrl(row.sourceUrl)" target="_blank" rel="noopener noreferrer">{{ row.sourceUrl }}</a><span v-else>{{ row.sourceUrl }}</span></div><div>原文：{{ row.excerpt }}</div><div>观察：{{ row.observation }}</div><div class="hint">局限：{{ row.limitation }}</div><div v-if="row.reviewReason" class="hint">审核理由：{{ row.reviewReason }}</div><el-button v-if="canReview && row.status === 'pending'" size="small" @click="reviewContentEvidence(candidate, row, 'accepted')">审核通过</el-button><el-button v-if="canReview && row.status === 'pending'" size="small" @click="reviewContentEvidence(candidate, row, 'rejected')">驳回</el-button></el-card></div>
        </el-card>
        <el-card v-if="canManage" class="candidate-card" shadow="never">
          <template #header>新增企业身份候选</template>
          <el-form label-position="top">
            <el-form-item label="候选主体名称"><el-input v-model="draftName" maxlength="300" placeholder="按证据原文填写，不从域名推断" /></el-form-item>
            <el-form-item label="证据来源"><el-select v-model="draftSource" style="width:100%"><el-option label="ICP备案" value="icp" /><el-option label="公开网站" value="public_site" /><el-option label="登记资料" value="registry" /><el-option label="第三方页面（不可单独确认）" value="third_party" /><el-option label="人工备注（不可单独确认）" value="manual_note" /></el-select></el-form-item>
            <el-form-item label="来源链接或记录编号"><el-input v-model="draftReference" maxlength="1000" /></el-form-item>
            <el-form-item label="证据原文"><el-input v-model="draftClaim" type="textarea" :rows="3" maxlength="2000" /></el-form-item>
            <el-form-item label="采集时间"><el-date-picker v-model="draftObservedAt" type="datetime" style="width:100%" /></el-form-item>
            <el-button type="primary" :loading="saving" :disabled="!draftName.trim() || !draftReference.trim() || !draftClaim.trim()" @click="saveCandidate">保存候选与证据</el-button>
          </el-form>
          <p class="hint">外部来源由操作人录入，系统当前不自动验证真伪。提交只产生待审候选，不会创建 CRM 客户、线索或商机。</p>
        </el-card>
      </template>
    </el-drawer>
    <el-dialog v-model="evidenceDialog" title="追加身份依据" width="520px">
      <el-form label-position="top"><el-form-item label="来源类型"><el-select v-model="draftSource" style="width:100%"><el-option label="ICP备案" value="icp" /><el-option label="公开网站" value="public_site" /><el-option label="登记资料" value="registry" /><el-option label="第三方页面（不可单独确认）" value="third_party" /><el-option label="人工备注" value="manual_note" /></el-select></el-form-item><el-form-item label="来源链接或记录编号"><el-input v-model="draftReference" maxlength="1000" /></el-form-item><el-form-item label="证据原文"><el-input v-model="draftClaim" type="textarea" maxlength="2000" /></el-form-item><el-form-item label="采集时间"><el-date-picker v-model="draftObservedAt" type="datetime" style="width:100%" /></el-form-item></el-form>
      <template #footer><el-button @click="evidenceDialog = false">取消</el-button><el-button type="primary" :loading="saving" :disabled="!draftReference.trim() || !draftClaim.trim()" @click="saveEvidence">保存追加证据</el-button></template>
    </el-dialog>
    <el-dialog v-model="contentDialog" title="录入网站内容观察" width="600px"><el-alert title="这只是人工观察，不是 AI 平台推荐测试。请复制公开页面原文，写明判断局限。" type="info" :closable="false" /><el-form label-position="top" class="candidate-card"><el-form-item label="关联技术报告"><span>版本 {{ contentReport?.version }}</span></el-form-item><el-form-item label="证据页面 HTTPS 链接"><el-input v-model="contentDraft.sourceUrl" maxlength="500" /></el-form-item><el-form-item label="页面原文摘录"><el-input v-model="contentDraft.excerpt" type="textarea" maxlength="2000" show-word-limit /></el-form-item><el-form-item label="人工观察结论"><el-input v-model="contentDraft.observation" type="textarea" maxlength="2000" show-word-limit /></el-form-item><el-form-item label="判断局限"><el-input v-model="contentDraft.limitation" type="textarea" maxlength="1000" show-word-limit /></el-form-item><el-form-item label="观察时间"><el-date-picker v-model="contentDraft.observedAt" type="datetime" /></el-form-item></el-form><template #footer><el-button @click="contentDialog = false">取消</el-button><el-button type="primary" :loading="contentSaving" :disabled="!contentDraft.sourceUrl.trim() || !contentDraft.excerpt.trim() || !contentDraft.observation.trim() || !contentDraft.limitation.trim() || !contentDraft.observedAt" @click="saveContentEvidence">保存待审证据</el-button></template></el-dialog>
    <IdentityCandidateBatchDialog v-model="batchDialog" @imported="loadAssets" />
    <el-dialog v-model="contactDialog" :title="correctingContactId ? '更正联系人候选' : '录入联系人候选'" width="560px">
      <el-alert title="请填写实际公开来源；所有填写的字段都会标记为此来源。录入后仍需人工审核。" type="info" :closable="false" />
      <el-form label-position="top" class="candidate-card">
        <el-form-item label="来源类型"><el-select v-model="contactDraft.sourceType"><el-option label="公开网站" value="public_site" /><el-option label="招聘页面" value="recruitment" /><el-option label="人工核对的公开页面" value="manual" /></el-select></el-form-item>
        <el-form-item label="来源链接（HTTPS）"><el-input v-model="contactDraft.sourceReference" /></el-form-item>
        <el-form-item label="姓名"><el-input v-model="contactDraft.name" /></el-form-item>
        <el-form-item label="职务"><el-input v-model="contactDraft.position" /></el-form-item>
        <el-form-item label="邮箱"><el-input v-model="contactDraft.email" /></el-form-item>
        <el-form-item label="电话"><el-input v-model="contactDraft.phone" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="contactDialog = false">取消</el-button><el-button type="primary" :loading="contactSaving" @click="saveContactCandidate">保存待审候选</el-button></template>
    </el-dialog>
    <el-dialog v-model="recheckHistoryDialog" title="网站复检设置记录" width="560px">
      <p>{{ recheckHistoryName }}</p>
      <el-table :data="recheckHistoryItems" v-loading="recheckHistoryLoading" stripe>
        <el-table-column label="操作" width="110"><template #default="{ row }">{{ row.enabled ? '开启复检' : '关闭复检' }}</template></el-table-column>
        <el-table-column label="操作时间" min-width="175"><template #default="{ row }">{{ formatTime(row.changedAt) }}</template></el-table-column>
        <el-table-column prop="changedBy" label="操作人 ID" width="115" />
      </el-table>
      <p v-if="!recheckHistoryLoading && !recheckHistoryItems.length" class="hint">尚无设置记录。</p>
    </el-dialog>
    <el-dialog v-model="chinazDialog" title="站长之家批量查询结果" width="min(920px, 96vw)">
      <el-alert title="查询结果只是第三方主体候选；保存前仍需预检，不能自动确认企业身份。" type="warning" :closable="false" />
      <el-table :data="chinazRows" max-height="360" stripe>
        <el-table-column prop="domain" label="域名" min-width="170" />
        <el-table-column prop="status" label="查询状态" width="135" />
        <el-table-column prop="proposedName" label="主体候选" min-width="190" />
        <el-table-column prop="reportedIcpNumber" label="页面备案号" min-width="160" />
        <el-table-column prop="note" label="说明" min-width="230" />
      </el-table>
      <div v-if="chinazPreview" class="hint">导入预检：可保存 {{ chinazPreview.ready }} 条；其他状态需逐条处理。</div>
      <template #footer><el-button @click="chinazDialog = false">关闭</el-button><el-button :disabled="!chinazRows.some(row => row.status === 'found')" :loading="chinazLoading" @click="previewChinazCandidates">预检待审候选</el-button><el-button type="primary" :disabled="!chinazPreview?.ready" :loading="chinazLoading" @click="importChinazCandidates">确认保存待审候选</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue';
import { ElMessage, ElMessageBox } from 'element-plus';
import { useRoute, useRouter } from 'vue-router';
import { getWebsiteSignalTarget } from '../../api/crm';
import { apiGet, apiPost, apiPut } from '../../api/http';
import { useAuthStore } from '../../stores/auth';
import IdentityCandidateBatchDialog from './IdentityCandidateBatchDialog.vue';
import AiPreparationPanel from './AiPreparationPanel.vue';
import ContactActionValue from './ContactActionValue.vue';
import { safeExternalHttpUrl } from '../../utils/externalUrl';

interface Asset { idText: string; batchId: string; domain?: string; hostname?: string; ipAddress?: string; port?: number; title?: string; cleanVerdict: string; cleanReasons?: string; observedAt: string; server?: string }
interface Icp { queryStatus: string; unitName?: string; serviceLicence?: string; mainLicence?: string }
interface Evidence { id: string; sourceType: string; sourceReference: string; claimText: string; observedAt: string; recordedAt: string }
interface ReviewEvent { id: string; action: string; actorId: string; reason: string; occurredAt: string }
interface Candidate { id: string; proposedName: string; status: string; version: number; createdBy: string; createdAt: string; evidence: Evidence[]; reviewEvents: ReviewEvent[] }
interface DiagnosticReport { id: string; version: number; status: string; trigger: string; requestedAt: string; attemptCount: number; error?: string; attemptsDataWarning?: string; comparedWithVersion?: number; changes: Array<{ key: string; previousStatus: string; currentStatus: string }>; checks: Array<{ key: string; status: string; evidence: string }> }
interface ContentEvidence { id: string; reportId: string; sourceUrl: string; excerpt: string; observation: string; limitation: string; status: string; observedAt: string; reviewReason?: string }
interface LeadPreview { ready: boolean; blockers: string[]; note: string; version: number; existingLead?: { id: string } }
interface ChinazRow { assetId: string; domain: string; status: string; proposedName?: string; reportedIcpNumber?: string; sourceReference: string; observedAt?: string; note: string }
interface ChinazBatchResult { configured: boolean; items: ChinazRow[] }
interface BatchPreview { ready: number; created: number; items: Array<{ status: string; reason: string }> }
interface RecheckRow { candidateId: string; proposedName: string; domain: string; crmStage: string; customerStatus?: number; schedule: { enabled: boolean; nextDueAt?: string }; latestReportId?: string; latestReportStatus?: string; latestReportAt?: string }
interface RecheckHistoryItem { enabled: boolean; intervalDays: number; changedAt: string; changedBy: string }
interface ContactCandidateRow { id: string; name?: string; position?: string; email?: string; phone?: string; sourceType: string; sourceReference: string; status: string; expiresAt?: string }
interface SourceStatusRow { code: string; name: string; mode: string; status: string; canRequestNow: boolean; availableFields: string; authorization: string; quota: string; cost: string; retention: string; note: string }
const auth = useAuthStore();
const router = useRouter();
const route = useRoute();
const focusedSignalId = ref('');
const focusedReportId = ref('');
const focusedEvidenceId = ref('');
const canManage = computed(() => auth.user?.permissionCodes.includes('crm.lead.manage') ?? false);
const canConfigureChinaz = computed(() => canManage.value && (auth.user?.permissionCodes.includes('identity.role.assign') ?? false));
const canReview = computed(() => auth.user?.permissionCodes.includes('crm.identity.review') ?? false);
const canManageContacts = computed(() => auth.user?.permissionCodes.includes('crm.contact.manage') ?? false);
const contactCandidates = ref<Record<string, ContactCandidateRow[]>>({});
const sourceStatuses = ref<SourceStatusRow[]>([]);
const sourceLoading = ref(false);
const contactDialog = ref(false);
const contactSaving = ref(false);
const contactIdentity = ref<Candidate | null>(null);
const correctingContactId = ref<string | null>(null);
const contactDraft = ref({ name: '', position: '', email: '', phone: '', sourceType: 'public_site', sourceReference: '' });
const assets = ref<Asset[]>([]);
const total = ref(0);
const loading = ref(false);
const keyword = ref('');
const verdict = ref('');
const page = ref(1);
const pageSize = 50;
const drawer = ref(false);
const selected = ref<Asset | null>(null);
const icp = ref<Icp | null>(null);
const icpCheckedAt = ref('');
const icpError = ref('');
const icpLoading = ref(false);
const candidates = ref<Candidate[]>([]);
const candidateLoading = ref(false);
const candidateError = ref('');
const reports = ref<Record<string, DiagnosticReport[]>>({});
const contentEvidence = ref<Record<string, ContentEvidence[]>>({});
const contentReviewDue = ref<Record<string, string | null>>({});
const contentDialog = ref(false);
const contentSaving = ref(false);
const contentCandidate = ref<Candidate | null>(null);
const contentReport = ref<DiagnosticReport | null>(null);
const contentDraft = ref({ sourceUrl: '', excerpt: '', observation: '', limitation: '', observedAt: new Date() });
const previews = ref<Record<string, LeadPreview>>({});
const saving = ref(false);
const draftName = ref('');
const draftSource = ref('manual_note');
const draftReference = ref('');
const draftClaim = ref('');
const draftObservedAt = ref(new Date());
const evidenceDialog = ref(false);
const batchDialog = ref(false);
const chinazConfigured = ref(false);
const chosenAssets = ref<Asset[]>([]);
const chinazLoading = ref(false);
const chinazDialog = ref(false);
const chinazRows = ref<ChinazRow[]>([]);
const chinazPreview = ref<BatchPreview | null>(null);
const recheckRows = ref<RecheckRow[]>([]);
const chosenRechecks = ref<RecheckRow[]>([]);
const recheckTotal = ref(0);
const recheckPage = ref(1);
const recheckStageFilter = ref('');
const recheckLoading = ref(false);
const recheckSaving = ref(false);
const recheckHistoryDialog = ref(false);
const recheckHistoryLoading = ref(false);
const recheckHistoryName = ref('');
const recheckHistoryItems = ref<RecheckHistoryItem[]>([]);
const evidenceCandidate = ref<Candidate | null>(null);
const lookupHost = computed(() => {
  const host = (selected.value?.domain || selected.value?.hostname || '').trim().toLowerCase();
  return /^[a-z0-9-]+(?:\.[a-z0-9-]+)+$/.test(host) && !/^\d+(?:\.\d+){3}$/.test(host) ? host : '';
});
function formatTime(value?: string) { return value ? new Date(value).toLocaleString() : '未知'; }
function sourceStatusLabel(status: string) { return ({ connection_configured: '连接已配置', switch_enabled: '开关已开启', local_tool_found: '本机工具已找到', not_configured: '未配置' } as Record<string, string>)[status] || status; }
async function loadSourceStatuses() {
  sourceLoading.value = true;
  try {
    const response = await apiGet<{ items: SourceStatusRow[] }>('/api/v1/crm/identity-candidates/sources/status');
    sourceStatuses.value = response.items || [];
  } catch (error: any) { ElMessage.error(error?.detail || '数据源状态读取失败'); }
  finally { sourceLoading.value = false; }
}
async function loadContactCandidates(candidate: Candidate) {
  try {
    const result = await apiPost<{ items: ContactCandidateRow[] }>(`/api/v1/crm/contacts/candidates/by-identity/${candidate.id}/view`, {});
    contactCandidates.value[candidate.id] = result.items || [];
  } catch (error: any) { ElMessage.error(error?.detail || '联系人候选加载失败'); }
}
async function contactReason(title: string) {
  const result = await ElMessageBox.prompt('请输入至少 10 字的操作理由', title, {
    inputValidator: value => value.trim().length >= 10 || '理由至少 10 字'
  });
  return result.value.trim();
}
async function revealContact(row: ContactCandidateRow) {
  try {
    const reason = await contactReason('查看来源详情');
    const full = await apiPost<{ name?: string; position?: string; email?: string; phone?: string; sourceReference: string }>(
      `/api/v1/crm/contacts/candidates/${row.id}/reveal`, { reason });
    await ElMessageBox.alert(`姓名：${full.name || '未知'}\n职务：${full.position || '未知'}\n邮箱：${full.email || '未知'}\n电话：${full.phone || '未知'}\n来源：${full.sourceReference}`, '联系人候选详情');
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '查看失败'); }
}
async function showContactEvents(row: ContactCandidateRow) {
  try {
    const result = await apiGet<{ items: Array<{ action: string; reason: string; actorId: number; occurredAt: string }> }>(
      `/api/v1/crm/contacts/candidates/${row.id}/events`);
    const lines = result.items.map(item => `${formatTime(item.occurredAt)} · ${item.action} · 操作人 ${item.actorId} · ${item.reason}`);
    await ElMessageBox.alert(lines.join('\n') || '暂无操作记录', '联系人候选操作记录');
  } catch (error: any) { ElMessage.error(error?.detail || '操作记录读取失败'); }
}
async function openContactForm(candidate: Candidate, row?: ContactCandidateRow) {
  contactIdentity.value = candidate;
  correctingContactId.value = row?.id || null;
  contactDraft.value = { name: '', position: '', email: '', phone: '', sourceType: row?.sourceType || 'public_site', sourceReference: row?.sourceReference || '' };
  if (row) {
    try {
      const reason = await contactReason('更正前查看原记录');
      const full = await apiPost<{ name?: string; position?: string; email?: string; phone?: string; sourceReference: string }>(
        `/api/v1/crm/contacts/candidates/${row.id}/reveal`, { reason });
      contactDraft.value = { ...contactDraft.value, name: full.name || '', position: full.position || '',
        email: full.email || '', phone: full.phone || '', sourceReference: full.sourceReference };
    } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '原记录读取失败'); return; }
  }
  contactDialog.value = true;
}
async function saveContactCandidate() {
  if (!contactIdentity.value) return;
  const draft = contactDraft.value;
  const fields: Record<string, string> = {};
  for (const key of ['name', 'position', 'email', 'phone'] as const) if (draft[key].trim()) fields[key] = draft.sourceReference.trim();
  const candidate = { identityCandidateId: contactIdentity.value.id,
    name: draft.name.trim() || null, position: draft.position.trim() || null,
    email: draft.email.trim() || null, phone: draft.phone.trim() || null,
    sourceType: draft.sourceType, sourceReference: draft.sourceReference.trim(),
    fieldSources: fields, observedAt: new Date().toISOString() };
  contactSaving.value = true;
  try {
    if (correctingContactId.value) {
      const reason = await contactReason('更正候选信息');
      await apiPost(`/api/v1/crm/contacts/candidates/${correctingContactId.value}/correct`, { candidate, reason });
    } else await apiPost('/api/v1/crm/contacts/candidates', candidate);
    contactDialog.value = false;
    ElMessage.success('待审候选已保存');
    await loadContactCandidates(contactIdentity.value);
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '保存联系人候选失败'); }
  finally { contactSaving.value = false; }
}
async function reviewContact(candidate: Candidate, row: ContactCandidateRow, action: 'confirm' | 'reject') {
  try {
    const reason = await contactReason(action === 'confirm' ? '确认联系人候选' : '驳回联系人候选');
    await apiPost(`/api/v1/crm/contacts/candidates/${row.id}/review`, { action, reason });
    await loadContactCandidates(candidate);
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '审核失败'); }
}
async function promoteContact(candidate: Candidate, row: ContactCandidateRow) {
  try {
    const reason = await contactReason('转为正式联系人');
    const result = await apiPost<{ contactId: string }>(`/api/v1/crm/contacts/candidates/${row.id}/promote`, { reason });
    ElMessage.success(`已转为正式联系人 ${result.contactId}`); await loadContactCandidates(candidate);
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '转联系人失败'); }
}
async function redactContact(candidate: Candidate, row: ContactCandidateRow) {
  try {
    const reason = await contactReason('删除候选个人信息');
    await ElMessageBox.confirm('将清除这条候选记录中的个人信息，操作记录会保留。如已转为正式联系人，还需到客户联系人中另行处理。', '确认删除', { type: 'warning' });
    await apiPost(`/api/v1/crm/contacts/candidates/${row.id}/redact`, { reason });
    await loadContactCandidates(candidate);
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '删除失败'); }
}
function reportStatusName(status: string) { return ({ queued: '排队中', running: '检查中', completed: '已完成', failed: '失败' } as Record<string, string>)[status] || status; }
async function showRecheckHistory(row: RecheckRow) {
  recheckHistoryName.value = `${row.proposedName} · ${row.domain}`;
  recheckHistoryItems.value = [];
  recheckHistoryDialog.value = true;
  recheckHistoryLoading.value = true;
  try {
    const result = await apiGet<{ items: RecheckHistoryItem[] }>(`/api/v1/crm/identity-candidates/${row.candidateId}/website-recheck-history`);
    recheckHistoryItems.value = result.items;
  } catch (error: any) { ElMessage.error(error?.detail || '设置记录读取失败'); }
  finally { recheckHistoryLoading.value = false; }
}
function recheckStageName(row: RecheckRow) {
  if (row.crmStage === 'customer') return row.customerStatus === 3 ? '已成交客户' : '已转客户';
  return row.crmStage === 'lead' ? '已成线索' : '仅身份确认';
}
function resetRecheckPage() { recheckPage.value = 1; loadRechecks(); }
async function loadRechecks() {
  if (!canManage.value) return;
  recheckLoading.value = true;
  try {
    const query = new URLSearchParams({ page: String(recheckPage.value) });
    if (recheckStageFilter.value) query.set('stage', recheckStageFilter.value);
    const result = await apiGet<{ items: RecheckRow[]; total: number }>(`/api/v1/crm/identity-candidates/website-rechecks?${query}`);
    recheckRows.value = result.items; recheckTotal.value = result.total;
  } catch (error: any) { ElMessage.error(error?.detail || '复检计划读取失败'); }
  finally { recheckLoading.value = false; }
}
async function saveRecheckBatch(enabled: boolean) {
  if (!canConfigureChinaz.value || !chosenRechecks.value.length) return;
  if (enabled === true) {
    try { await ElMessageBox.confirm(`对所选 ${chosenRechecks.value.length} 个网站开启每 7 天一次的公开页面技术复检？`, '确认开启', { type: 'warning' }); }
    catch { return; }
  }
  recheckSaving.value = true;
  try {
    const result = await apiPut<{ updated: number }>('/api/v1/crm/identity-candidates/website-rechecks/batch', {
      candidateIds: chosenRechecks.value.map(row => row.candidateId), enabled
    });
    ElMessage.success(`已更新 ${result.updated} 个网站`);
    chosenRechecks.value = []; await loadRechecks();
  } catch (error: any) { ElMessage.error(error?.detail || '批量设置失败'); }
  finally { recheckSaving.value = false; }
}
async function loadChinazStatus() {
  if (!canManage.value) return;
  try {
    const status = await apiGet<{ configured: boolean }>('/api/v1/crm/identity-candidates/chinaz/status');
    chinazConfigured.value = status.configured;
  }
  catch { chinazConfigured.value = false; }
}
function chinazCandidateRows() {
  return chinazRows.value.filter(row => row.status === 'found' && row.proposedName && row.observedAt).map(row => ({
    assetId: row.assetId, domain: row.domain, proposedName: row.proposedName,
    sourceReference: row.sourceReference,
    claimText: `站长之家页面显示主体候选：${row.proposedName}${row.reportedIcpNumber ? `；页面备案号：${row.reportedIcpNumber}` : ''}`,
    observedAt: row.observedAt
  }));
}
async function lookupChinazBatch() {
  if (!chinazConfigured.value || chosenAssets.value.length < 1 || chosenAssets.value.length > 10) return;
  chinazLoading.value = true; chinazPreview.value = null;
  try {
    const result = await apiPost<ChinazBatchResult>('/api/v1/crm/identity-candidates/chinaz/batch-lookup', { assetIds: chosenAssets.value.map(asset => asset.idText) });
    chinazRows.value = result.items; chinazDialog.value = true;
  } catch (error: any) { ElMessage.error(error?.detail || '批量查询失败'); }
  finally { chinazLoading.value = false; }
}
async function previewChinazCandidates() {
  chinazLoading.value = true; chinazPreview.value = null;
  try { chinazPreview.value = await apiPost<BatchPreview>('/api/v1/crm/identity-candidates/batch-preview', { items: chinazCandidateRows() }); }
  catch (error: any) { ElMessage.error(error?.detail || '候选预检失败'); }
  finally { chinazLoading.value = false; }
}
async function importChinazCandidates() {
  if (!chinazPreview.value?.ready) return;
  try { await ElMessageBox.confirm(`将 ${chinazPreview.value.ready} 条结果保存为待审候选；不确认企业身份。`, '确认保存', { type: 'warning' }); }
  catch { return; }
  chinazLoading.value = true;
  try {
    const result = await apiPost<BatchPreview>('/api/v1/crm/identity-candidates/batch-import', { items: chinazCandidateRows() });
    ElMessage.success(`已保存 ${result.created} 条待审候选`); chinazPreview.value = null; await loadCandidates();
  } catch (error: any) { ElMessage.error(error?.detail || '保存失败，请重新预检'); }
  finally { chinazLoading.value = false; }
}
function openChinaz() {
  if (!lookupHost.value) return;
  const url = `https://seo.chinaz.com/${lookupHost.value}`;
  window.open(url, '_blank', 'noopener,noreferrer');
  draftSource.value = 'third_party'; draftReference.value = url;
  draftName.value = ''; draftClaim.value = ''; draftObservedAt.value = new Date();
}
function searchAssets() { page.value = 1; loadAssets(); }
async function loadAssets() {
  loading.value = true;
  try {
    const response = await apiGet<{ total: number; items: Asset[] }>('/api/v1/crm/quake/assets', {
      params: { keyword: keyword.value.trim() || undefined, verdict: verdict.value || undefined, skip: (page.value - 1) * pageSize, take: pageSize }
    });
    assets.value = response.items || []; total.value = response.total || 0;
  }
  catch (error: any) { ElMessage.error(error?.detail || '待审资产加载失败'); }
  finally { loading.value = false; }
}
function selectAsset(asset: Asset) { focusedSignalId.value = ''; focusedReportId.value = ''; focusedEvidenceId.value = ''; selected.value = asset; icp.value = null; icpError.value = ''; icpCheckedAt.value = ''; candidates.value = []; candidateError.value = ''; reports.value = {}; contentEvidence.value = {}; contentReviewDue.value = {}; previews.value = {}; drawer.value = true; return loadCandidates(); }
async function lookupIcp() {
  if (!lookupHost.value) return;
  const host = lookupHost.value;
  icpLoading.value = true; icp.value = null; icpError.value = '';
  try {
    const result = await apiPost<{ items: Icp[] }>('/api/v1/crm/icp/lookup', [host]);
    if (result.items?.[0]?.queryStatus === 'disabled' || result.items?.[0]?.queryStatus === 'not_configured') {
      icpError.value = result.items[0].queryStatus === 'disabled'
        ? 'ICP 查询尚未启用。' : '服务端尚未配置 ICP 查询工具。';
      return;
    }
    if (selected.value && lookupHost.value === host) {
      icp.value = result.items?.[0] || null; icpCheckedAt.value = new Date().toISOString();
      if (icp.value?.queryStatus === 'found' && icp.value.unitName) {
        draftName.value = icp.value.unitName; draftSource.value = 'icp';
        draftReference.value = `beian:${host}:${icp.value.serviceLicence || icp.value.mainLicence || 'unknown'}`;
        draftClaim.value = `ICP备案查询返回主办单位：${icp.value.unitName}`;
        draftObservedAt.value = new Date();
      }
    }
  } catch (error: any) { icpError.value = error?.detail || 'ICP 查询失败，请检查服务端能力。'; }
  finally { icpLoading.value = false; }
}
function sourceText(type: string) { return ({ icp: 'ICP备案', public_site: '公开网站', registry: '登记资料', third_party: '第三方页面（待核验）', manual_note: '人工备注' } as Record<string, string>)[type] || type; }
function statusText(status: string) { return ({ pending: '待审核', confirmed: '已确认', rejected: '已驳回', confirm: '确认身份', reject: '驳回', reopen: '撤销审核' } as Record<string, string>)[status] || status; }
async function loadCandidates() {
  const assetId = selected.value?.idText;
  if (!assetId) return;
  candidateLoading.value = true; candidateError.value = '';
  try {
    const response = await apiGet<{ items: Candidate[] }>(`/api/v1/crm/identity-candidates/by-asset/${assetId}`);
    if (selected.value?.idText === assetId) candidates.value = response.items || [];
  } catch (error: any) { candidateError.value = error?.detail || '身份候选加载失败，请确认后端已更新迁移。'; }
  finally { candidateLoading.value = false; }
}
function evidenceInput() { return { sourceType: draftSource.value, sourceReference: draftReference.value.trim(), claimText: draftClaim.value.trim(), observedAt: draftObservedAt.value.toISOString() }; }
async function saveCandidate() {
  if (!selected.value?.idText) return;
  saving.value = true;
  try {
    await apiPost(`/api/v1/crm/identity-candidates/by-asset/${selected.value.idText}`, { proposedName: draftName.value.trim(), evidence: evidenceInput() });
    ElMessage.success('身份候选已保存，等待审核'); await loadCandidates();
    draftName.value = ''; draftReference.value = ''; draftClaim.value = ''; draftSource.value = 'manual_note';
  } catch (error: any) { ElMessage.error(error?.detail || '保存失败'); }
  finally { saving.value = false; }
}
function openEvidence(candidate: Candidate) { evidenceCandidate.value = candidate; draftSource.value = 'manual_note'; draftReference.value = ''; draftClaim.value = ''; draftObservedAt.value = new Date(); evidenceDialog.value = true; }
async function saveEvidence() {
  if (!evidenceCandidate.value) return;
  saving.value = true;
  try { await apiPost(`/api/v1/crm/identity-candidates/${evidenceCandidate.value.id}/evidence`, evidenceInput()); evidenceDialog.value = false; ElMessage.success('证据已追加'); await loadCandidates(); }
  catch (error: any) { ElMessage.error(error?.detail || '追加证据失败'); }
  finally { saving.value = false; }
}
async function review(candidate: Candidate, action: 'confirm' | 'reject' | 'reopen') {
  try {
    const { value } = await ElMessageBox.prompt('请输入至少 10 字的审核理由；决定将保留在审核历史中。', statusText(action), { inputValidator: text => text.trim().length >= 10 || '审核理由至少 10 字' });
    await apiPost(`/api/v1/crm/identity-candidates/${candidate.id}/review`, { action, reason: value.trim(), expectedVersion: candidate.version });
    ElMessage.success('审核决定已保存'); await loadCandidates();
  } catch (error: any) { if (error !== 'cancel') { ElMessage.error(error?.detail || '审核失败，请刷新后重试'); await loadCandidates(); } }
}
async function loadReports(candidate: Candidate) {
  try { const response = await apiGet<{ items: DiagnosticReport[] }>(`/api/v1/crm/identity-candidates/${candidate.id}/website-reports`); reports.value[candidate.id] = response.items || []; await loadContentEvidence(candidate); }
  catch (error: any) { ElMessage.error(error?.detail || '报告加载失败'); }
}
async function loadContentEvidence(candidate: Candidate) {
  try { const response = await apiGet<{ items: ContentEvidence[]; reviewDueReportId: string | null }>(`/api/v1/crm/identity-candidates/${candidate.id}/content-evidence`); contentEvidence.value[candidate.id] = response.items || []; contentReviewDue.value[candidate.id] = response.reviewDueReportId; }
  catch (error: any) { ElMessage.error(error?.detail || '内容证据加载失败'); }
}
function reportVersion(candidateId: string, reportId: string) { return reports.value[candidateId]?.find(row => row.id === reportId)?.version ?? reportId; }
function openDueContentEvidence(candidate: Candidate) {
  const report = reports.value[candidate.id]?.find(row => row.id === contentReviewDue.value[candidate.id]);
  if (report) openContentEvidence(candidate, report);
  else ElMessage.warning('请先刷新诊断历史');
}
function openContentEvidence(candidate: Candidate, report: DiagnosticReport) {
  contentCandidate.value = candidate; contentReport.value = report;
  contentDraft.value = { sourceUrl: `https://${selected.value?.domain || selected.value?.hostname}/`, excerpt: '', observation: '', limitation: '', observedAt: new Date() };
  contentDialog.value = true;
}
async function saveContentEvidence() {
  if (!contentCandidate.value || !contentReport.value) return;
  contentSaving.value = true;
  try {
    await apiPost(`/api/v1/crm/identity-candidates/${contentCandidate.value.id}/content-evidence`, {
      reportId: contentReport.value.id, ...contentDraft.value,
      observedAt: contentDraft.value.observedAt.toISOString(),
    });
    contentDialog.value = false; ElMessage.success('内容证据已保存，等待审核'); await loadContentEvidence(contentCandidate.value);
  } catch (error: any) { ElMessage.error(error?.detail || '内容证据保存失败'); }
  finally { contentSaving.value = false; }
}
async function reviewContentEvidence(candidate: Candidate, row: ContentEvidence, decision: 'accepted' | 'rejected') {
  try {
    const { value } = await ElMessageBox.prompt(decision === 'accepted' ? '填写审核理由；通过只表示这条页面证据可以用于人工分析。' : '填写驳回理由；这条证据将保留在审核记录中。', decision === 'accepted' ? '审核通过' : '驳回证据', { inputValidator: text => text.trim().length >= 10 || '审核理由至少 10 字' });
    await apiPost(`/api/v1/crm/identity-candidates/${candidate.id}/content-evidence/${row.id}/review`, { decision, reason: value.trim() });
    ElMessage.success('审核结果已保存'); await loadContentEvidence(candidate);
  } catch (error: any) { if (error !== 'cancel') ElMessage.error(error?.detail || '审核失败'); }
}
async function requestReport(candidate: Candidate) {
  try {
    const result = await apiPost<{ cooledDown: boolean }>(`/api/v1/crm/identity-candidates/${candidate.id}/website-reports`, { url: `https://${selected.value?.domain || selected.value?.hostname}/` });
    ElMessage.info(result.cooledDown ? '一小时内已有诊断任务，已返回现有报告' : '诊断已排队，请稍后刷新报告'); await loadReports(candidate);
  } catch (error: any) { ElMessage.error(error?.detail || '诊断申请失败'); }
}
async function loadPreview(candidate: Candidate) {
  try { previews.value[candidate.id] = await apiGet<LeadPreview>(`/api/v1/crm/identity-candidates/${candidate.id}/lead-preview`); }
  catch (error: any) { ElMessage.error(error?.detail || '转线索预检失败'); }
}
async function promoteLead(candidate: Candidate) {
  if (!previews.value[candidate.id]?.ready) return;
  try {
    await ElMessageBox.confirm('将以已确认主体名称创建一条 CRM 线索。信用代码和联系人保持未知，后续仍需销售评估。', '确认转线索', { type: 'warning' });
    const result = await apiPost<{ leadId: string }>(`/api/v1/crm/identity-candidates/${candidate.id}/promote-lead`, { expectedVersion: candidate.version });
    ElMessage.success(`线索已创建：${result.leadId}`); await loadCandidates(); await loadPreview(candidate);
  } catch (error: any) { if (error !== 'cancel') { ElMessage.error(error?.detail || '转线索失败，请重新预检'); await loadPreview(candidate); } }
}
function goToLeadAssessment(name: string) { drawer.value = false; router.push({ path: '/crm/leads', query: { keyword: name } }); }
onMounted(async () => {
  await Promise.all([loadAssets(), loadChinazStatus()]);
  const signalId = route.query.websiteSignalId;
  if (typeof signalId !== 'string' || !/^\d+$/.test(signalId)) return;
  try {
    const target = await getWebsiteSignalTarget(signalId);
    await selectAsset(target.asset);
    const candidate = candidates.value.find(row => row.id === target.candidateId);
    if (!candidate) throw new Error('对应企业身份已不可访问');
    await loadReports(candidate);
    if (!reports.value[candidate.id]?.some(row => row.id === target.reportId)) {
      const report = await apiGet<DiagnosticReport>(`/api/v1/crm/identity-candidates/${candidate.id}/website-reports/${target.reportId}`);
      reports.value[candidate.id] = [report, ...(reports.value[candidate.id] || [])];
    }
    if (target.evidenceId && !contentEvidence.value[candidate.id]?.some(row => row.id === target.evidenceId)) {
      const evidence = await apiGet<ContentEvidence>(`/api/v1/crm/identity-candidates/${candidate.id}/content-evidence/${target.evidenceId}`);
      contentEvidence.value[candidate.id] = [evidence, ...(contentEvidence.value[candidate.id] || [])];
    }
    focusedSignalId.value = signalId;
    focusedReportId.value = target.reportId;
    focusedEvidenceId.value = target.evidenceId || '';
    await nextTick();
    document.getElementById(target.evidenceId ? `website-evidence-${target.evidenceId}` : `website-report-${target.reportId}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' });
  } catch (error: any) { ElMessage.error(error?.detail || error?.message || '提醒对应资料加载失败'); }
});
</script>

<style scoped>
.identity-desk { padding: 16px; }
.toolbar, .filters, .evidence-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.hint { color: #909399; font-size: 12px; line-height: 1.6; }
.filters, .evidence-head { margin: 16px 0; justify-content: flex-start; }
.pagination { margin-top: 12px; justify-content: flex-end; }
.candidate-card { margin-top: 12px; }
.candidate-actions { margin-top: 12px; }
.evidence-list { padding-left: 20px; line-height: 1.6; }
.candidate-actions { display: flex; flex-wrap: wrap; gap: 6px; }
.candidate-actions :deep(.el-button) { margin-left: 0; }
.report-diff { margin: 8px 0; padding: 8px 10px; border-left: 3px solid #409eff; background: #f4f8ff; font-size: 13px; line-height: 1.7; }
.signal-focus { border: 2px solid #409eff; }
.signal-return { margin: 0 0 12px; }
</style>

<style>
.identity-drawer .el-descriptions__content,
.identity-drawer .evidence-list,
.identity-drawer .hint { overflow-wrap: anywhere; word-break: break-word; }
@media (max-width: 600px) {
  .identity-drawer .el-drawer__body { padding: 12px; }
  .identity-drawer .el-descriptions__label { width: 90px; }
}
</style>
