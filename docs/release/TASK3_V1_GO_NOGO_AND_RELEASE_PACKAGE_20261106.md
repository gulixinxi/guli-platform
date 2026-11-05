# TASK 3 交付包 —— Guli Platform V1 上线就绪判定与可执行上线包

- 生成时间：2026-11-06（所有结论均来自本轮实跑，不引用历史 PASS）
- 仓库：`D:\guli\projects\guli-platform`，分支 `master`
- HEAD：`2ccf62d`（本地领先 `origin/master` 2 个提交，**未 push**）
- 隔离验证目标：`127.0.0.1:8081`（应用）+ `127.0.0.1:55460`（PostgreSQL 17.11 全新集群）
- 口径声明：本文中所有 PASS/FAIL 均由本轮命令的退出码与实测响应码给出；历史报告的 PASS 一律标注为历史记录。

---

## 0. 判定结论（先给结论）

| 判定项 | 结论 |
|---|---|
| **V1 生产上线（GO / NO-GO）** | **NO-GO** |
| 代码与构建可交付性 | **GO**（洁净部署全链通、闸门自愈、全量回归绿） |
| 运行时/业务可用性 | **NO-GO**（生产部署面当前 DOWN；生产库零主数据；订单幂等重放 500） |
| 回退可执行性 | **GO**（备份独立复算 PASS，回退工件哈希吻合） |
| 需要下一轮再做 | **2 个 P1 缺陷 + 1 个主数据前置 + 1 次 push 批准**，不需要架构改造 |

**一句话**：代码、迁移、权限、CRM/ERP 业务链路**已被真实运行证明可用**；**阻止上线的是运维状态而不是代码** —— 生产部署面没在跑、生产库一条主数据都没有、销售订单幂等重放会 500。修掉这 4 项后可直接复跑本包的验收表转 GO。

---

## 1. 本轮实际变更与文件清单

### 1.1 提交（本轮共 2 个聚焦提交，均未 push）

| 提交 | 文件 | 变更 | 目的 |
|---|---|---|---|
| `2ccf62d` | `tests/GuliERP.Api.Tests/MigrationChainIntegrityFacts.cs`（新增） | +348 | **P1-09 守卫落地**（见 §3.6） |
| `8281d93`（上一阶段） | `.codex/hooks.json`、`.codex/hooks/run-node-hook.cmd`（新增）、`tools/dev/release-v1-acceptance/final-verification.ps1`、`tools/dev/final-verification-drift-selftest.py`（新增） | +483 / -31 | 交付缺口闭合：新克隆可用 |

> 其余 **18 modified + 28 untracked** 全部原样保留，未清理、未重置、未改写既有 WIP；**0 文件删除**。

### 1.2 本轮实测命令与退出码

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet publish apps/api/GuliERP.Api -c Release -o <deploy>/api` | 0 | 55 个发布文件 |
| `npm run build`（apps/web） | 0 | 69 个 wwwroot 文件 |
| Production 启动（`ASPNETCORE_ENVIRONMENT=Production`，8081） | 0 | `/health/live` 200、`/health/ready` 200（foundation-db Healthy） |
| `dotnet test tests/GuliERP.Api.Tests` | 0 | **126/126 PASS**（119 原有 + 7 新守卫） |
| `python tools/dev/final-verification-drift-selftest.py` | 0 | **16 checks / 0 failures** |
| `pwsh tools/dev/release-v1-acceptance/final-verification.ps1 -OfflineOnly` | 1 | 9 passed / 1 failed / 1 skipped（唯一 FAIL = 未推送提交） |
| `seed-mdm masterdata --dry-run`（隔离库） | 0 | **写入 14 条编号规则 + 8 分类 + 1 仓库 + 4 库位** → 发现 dry-run 契约缺陷 |
| 备份全量 SHA-256 独立复算 | 0 | **58,409/58,409 一致，0 差异、0 缺失、0 空哈希，59 秒** |

---

## 2. 六个附加任务的逐项结果

### 任务 1 —— ERP/CRM 业务烟测

**结论：60 项检查 / 56 PASS / 4 FAIL**（合并台账：`D:/guli/isolated-v1-20261106/smoke-ledger-consolidated.json`）

| 业务段 | 结果 | 关键证据 |
|---|---|---|
| 认证（CSRF / 登录 / me / 错误口令 / 未知用户 / 未认证 401） | **6/6 PASS** | 未认证访问 CRM → 401 `authentication_required` |
| 用户权限强制矩阵 | **11/11 PASS** | 见 §2.1 |
| CRM 客户增改查 | **5/5 PASS** | 建号 201（单号 `CO202611060004`）→ 读 200 → 改 200 → 关键词查询命中 |
| 联系人与跟进关联 | **6/6 PASS** | 联系人建档 201 → `contacts/by-company/{id}` 命中 → 改 200；跟进建档 201 → `activities/by-company/{id}` 命中 → 今日待办聚合 200 |
| 客户到销售流程 | **8/8 PASS** | 线索 → **需求评估（confirmed + 结构化证据）** → 转客户 → 建商机 → 商机按客户过滤 → 商机维度跟进 |
| ERP 销售订单 | **8/10 PASS** | 建 201（`SO-20261106-000002`）→ 读 200 → 改 200（数量 3、单价 1100）→ 确认 200 → 列表命中 → 过期并发版本 400 → 未认证 401；**幂等重放 500（缺陷）** |
| 输入校验探针 | **0/1 PASS** | 超长 `creditCode`（26 字符 vs `varchar(18)`）→ **500 而非 400（缺陷）** |

**客户到销售流程必须走「需求评估」**：直接 `POST /api/v1/crm/deals` 被业务规则拒绝（400 `正式商机必须关联已确认的需求评估及当前版本，请从线索需求评估创建。`），本轮已用**反向对照**证明该规则真实生效（N04），并用合规路径建出商机（`DE202611060001`）。

#### 2.1 权限强制矩阵（全部真实 HTTP 实测，非单元测试）

| 用户 | 角色 | 目标 | 实际 | 判定 |
|---|---|---|---|---|
| `admin` | 全部 7 个 | 登录 / `/auth/me` | 200，44 个权限码 | PASS |
| `Gv1_crm_only` | CRM_OPERATOR | 建 CRM 客户 | 201 | **PASS（允许）** |
| `Gv1_crm_only` | CRM_OPERATOR | 读 ERP 销售订单 | 403 | **PASS（拒绝）** |
| `Gv1_sales_only` | ERP_SALES_OPERATOR | 建 CRM 客户 | 403 | **PASS（拒绝）** |
| `Gv1_sales_only` | ERP_SALES_OPERATOR | 读 ERP 销售订单 | 200 | **PASS（允许）** |
| `Gv1_no_role` | 无 | 读 CRM / ERP / MDM | 403 / 403 / 403 | **PASS（拒绝）** |
| 未认证 | — | 读 CRM / ERP | 401 / 401 | **PASS（拒绝）** |

权限解析路径已核实为**每次请求实时查库**（`PermissionAuthorizationHandler`），不是登录时快照 —— 撤权立即生效。

### 任务 2 —— 迁移安全性

- **53 个迁移全部应用成功**（crm 31 / identity 6 / mdm 11 / sales 2 / purchase 1 / doc_kernel 1 / foundation 1）。
- **schema 与正式库逐列完全一致**：fresh 937 列 vs formal 937 列，**双向 0 差异**。比对方式：把生产备份 dump 恢复到隔离集群的第二个库，只读文件、不碰生产。
- **7 个上下文 `has-pending-model-changes` 全部 in-sync**（`MODEL_NO_PENDING_CHANGES_ALL_CONTEXTS`）。
- **破坏性迁移**：全仓 `DropTable/DropColumn/DropIndex/DropForeignKey` **全部位于 `Down()` 反向方法**，前进路径 0 处破坏性操作（以 `20261018003755_InitCrmSchema.cs` 逐行核验：Down() 起于 501 行，其前 0 处、其后 6 处）。
- **P1-09**：CRM 31 步链连贯、无分叉、无 ID 冲突；全仓唯一 `CrmDbContextModelSnapshot.cs`。判定为**目录位置不一致**而非历史分叉 ⇒ 修法是**加守卫**不是搬目录（见 §3.6）。

### 任务 3 —— 项目改名兼容性

tracked 非 docs 文件中 `gulierp-next` 共 **11 处**，逐条判定**无一处影响实际执行**：

| 位置 | 性质 | 处置 |
|---|---|---|
| `README.md:83-85` | 旧仓 GitHub 链接（靠重定向仍可用） | V1.1 |
| `data/bootstrap/reference/manifest.json:6` + 2 个 discovery 脚本的 `extractor` 溯源串 | **篡改即篡改证据** | 不动 |
| 3 个测试文件的 XML 文档注释 | 注释 | 不动 |
| `final-verification.ps1:87` 旧 URL 重定向探测 | **有意保留**的兼容性检查 | 不动 |
| `db_inventory.json:8`、`GIT_COMMIT_PLAN.md:30` | 历史记录 | 不动 |

绝对路径形态 `guli\projects` 在 tracked 非 docs 文件中仅 **1 处**（同一份 `db_inventory.json`）。`.github/workflows` **不存在**（手工发布）。`appsettings.Development.json` 确已被跟踪，指向 HOLD 库但**内容是 `CHANGE_ME` 占位、无真实口令**，且 Production 不读该文件 ⇒ 不构成上线阻塞（V1.1 风险项）。

### 任务 4 —— 隔离环境洁净部署烟测

| 步骤 | 结果 |
|---|---|
| 全新 Release publish | exit 0，55 文件 |
| 前端 build → wwwroot | exit 0，69 文件 |
| Production 启动（**真 Production 路径**，走 `ReleaseDatabaseTargetGuard` 核验服务器上报身份） | `/health/live` 200、`/health/ready` 200 Healthy |
| 数据库迁移 | 53 个全部应用（空库 → 完整 schema） |
| 配置加载 | 连接串 + `GULIERP_RELEASE_DB_*` 五项环境变量即通过守卫；**未设** `GULIERP_DB_PASSWORD_CREDENTIAL_TARGET` ⇒ 凭据路径整体跳过，**零系统级变更、零凭据落盘** |
| ERP/CRM 登录 | 4 个用户全部登录成功 |
| 核心业务 | 见任务 1 |
| **重启后持久性** | **5/5 PASS**：客户 / 联系人 / 商机 / 线索 / ERP 订单在**停进程 → 同配置重启**后全部可读回 |

### 任务 5 —— 备份与回退

- **独立复算 PASS**（不引用历史报告）：清单 **58,409** 条，逐文件重算 SHA-256 → **58,409 一致 / 0 差异 / 0 缺失 / 0 空哈希 / 0 不可读**，耗时 59 秒。空哈希按硬失败处理（不 catch 后写空值）。
- **回退 DB 工件哈希吻合**：`guli-platform-v1-gulierp_next_prod-20261105T024244Z.dump`（247,402 B）实测 SHA-256 与 `stop-guli-platform.ps1` 记录值**逐字一致**。
- **回退工具就位**：`tools/dev/release-v1-backup.ps1`、`tools/dev/release-v1-restore-verify.ps1`（后者在一次性集群里恢复并比对表清单与行数，不写源库）。
- **未执行任何生产恢复**。

### 任务 6 —— V1.1 延后清单

见 §6。

---

## 3. P0/P1 阻塞台账

### 3.1 已解决（本轮）

| ID | 问题 | 解决方式 | 证据 |
|---|---|---|---|
| 交付缺口 | Windows Hook 启动脚本不在版本控制内 ⇒ 新克隆不可用 | 入库 `8281d93` | 全新克隆验收：3 个 `.mjs` + 启动器齐全，钩子实跑 exit 0、缺脚本 exit 2；克隆内校验器 10/0/1 |
| P1-09 | 迁移链「第二快照 / 断链」无自动化防线 | 新增守卫 `2ccf62d` | 7 项断言全绿，纳入 `dotnet test` |
| 离线校验器 | 缺少「未跟踪 runner 必须 FAIL」的负向判据 | 控制器收紧 + 我补 N6 正向对照 | 漂移自检 16/0 |

### 3.2 未解决 —— 阻止 V1 上线（4 项，全部可在本轮内闭环）

**B-1｜生产部署面当前 DOWN（P0）**
`D:\guli\platform-deploy\v1.0\verify-deploy-surface.ps1` → **0 passed / 11 failed**，`8080` 无监听（127.0.0.1:8080 / 192.168.2.200:8080 均不可达）。这不是本轮造成的（2026-11-05 基线即如此），但上线前必须启动并复跑该脚本拿到 11/11。
→ 需要控制者批准：**在生产端口启动生产实例**（§7 命令 A）。

**B-2｜生产库零主数据（P0）**
正式库实测：`mdm.*` 全部表 **0 行**（uom / item / item_category / business_partner / warehouse / location / dictionary_type / dictionary_item / country / administrative_region），`crm.*` / `sales.*` / `purchase.*` 也全 0，仅 3 条编号规则与身份数据存在。
直接后果（已实测）：ERP 销售订单的**客户/物料下拉为空**；业务伙伴填 `countryCode='CN'` 被拒（400 `mdm_business_partner_country_code_unknown`）—— 因为 `data/bootstrap/reference/system/country.json` 的 `items` 为 **0**（文件自述「V1 不应把行政区划 commit 到 Git，应在运行时从权威源加载」，`seed_status_at_file_level=NEEDS_EXTERNAL_STANDARD_UPDATE`）。
→ 需要控制者批准：**执行 MDM 播种 + 提供国家/区划参考数据来源**（§7 命令 B/C）。

**B-3｜销售订单 `Idempotency-Key` 重放返回 500（P1）**
同一 key + 同一 body 二次 `POST /api/v1/sales/orders` → **HTTP 500**，日志 `23505 duplicate key value violates unique constraint "ux_gulierp_sales_order_scope_orderno"`。
根因（代码级）：`modules/sales/GuliERP.Sales.Infrastructure/Sales/SalesOrderService.cs:93-120` —— `CreateDraftAsync` 只把 key 传给单号生成器（幂等返回同一单号），**随后无条件再插一条订单行**，撞唯一约束；没有「按 key 找回原订单并返回」的短路。
影响：真实网络抖动后的客户端重试必然 500。数据不会被写坏（唯一约束挡住了重复行），但用户体验是硬失败。
→ 修法（**需控制者批准**：改生产代码路径）：在 `CreateDraftAsync` 开头按 `(TenantId, CompanyId, OrderNo)` 回查已存在订单并直接返回该 DTO。

**B-4｜超长输入返回 500 而不是 400（P1，可降级到 V1.1）**
`POST /api/v1/crm/companies` 传 26 字符 `creditCode`（列宽 `varchar(18)`）→ **HTTP 500**（`Npgsql.PostgresException 22001 value too long for type character varying(18)`），而非 400 校验错误。
→ 修法：请求 DTO 加长度校验，或在服务层把 DB 长度异常映射为 400。

### 3.3 需要管理员窗口（不是本轮可做）

| ID | 事项 | 说明 |
|---|---|---|
| A-1 | R237 受控恢复准备 / R243 上线决策 | 集群 `7704352727349170728`、库 `gulierp_r237_isolated`、目标 `127.0.0.1:55451`；**本轮完全未触碰**（未删 PID、未 `pg_resetwal`、未覆盖数据） |
| A-2 | 生产库写操作 | 本轮对正式库只做 SELECT 与 dump 恢复比对，**未写入** |

### 3.4 需要控制者批准

见 §7。

### 3.5 已判定为「不在本轮范围」（诚实留档，不假装修好）

**P0-03 跨租户隔离不可隔离实施**（代码路径级证明，不是猜测）：
- Identity 侧给 `GuliErpUser` 加 fail-closed 租户过滤器会**破坏登录** —— `AuthenticationService.LoginAsync`（`modules/identity/GuliERP.Identity.Infrastructure/Authentication/AuthenticationService.cs:76`）先用 `FindByNameAsync` 取人（经 `UserStore` → `DbContext.Set<T>()` → 应用查询过滤器），而租户是**拿到用户之后**才由 `tenantCode` 解析（第 95-98 行），此刻 `CurrentTenantId = 0` ⇒ 过滤器会让所有人登不进去。
- CRM 侧全部 `IgnoreQueryFilters()` 调用点都在**后台 worker**（`WebsiteDiagnosticService` / `WebsiteRecheckService` / `ContactCandidateService` / `WebsiteContentSignalService`），本就跨租户运行，去掉会让后台任务静默失效。
⇒ 需要「认证前可解析租户上下文 + 受审计的跨租户逃生口」的架构改造，**超出「不扩展范围 / 不重设计」约束**，如实上报。

---

## 4. 生产库当前状态（只读实测，2026-11-06）

| 区域 | 行数 |
|---|---|
| `identity.*` | 租户 1 / 公司 1 / 工厂 1 / 组织 1 / 员工 1 / 用户 1 / 角色 7 / 角色权限声明 45 / 角色授权 7 |
| `mdm.*` | 编号规则 3；**其余全部 0** |
| `crm.*` / `sales.*` / `purchase.*` / `doc_kernel.*` | **全部 0** |

即：**正式库是一份「纯身份、零业务数据」的干净部署**。这让迁移风险极低（无数据可迁移），但意味着**主数据与业务数据必须在上线前播种**。

---

## 5. 业务烟测结果表（60 项）

| 段 | 编号 | 结果 |
|---|---|---|
| A 认证 | A01–A06 | 6 PASS |
| B 客户 CRUD（首轮，含测试数据错误） | B01–B05 | 已被 phase2 复跑取代 |
| C 联系人/跟进（首轮） | C01–C05 | 已被 phase2 复跑取代 |
| D 客户到销售（首轮） | D01–D05 | D01/D02 有效，其余被 phase3 复跑取代 |
| E ERP 订单（首轮） | E01–E04 | E01 有效，其余被 corrected 复跑取代 |
| F 权限矩阵 | F01–F11 | **11 PASS** |
| G MDM 可达性 | G01 | PASS |
| H MDM 播种（API） | H01–H06 | 4 PASS / **2 FAIL**（H04 国家参考数据缺失、H06 连带） |
| I 客户 CRUD（复跑） | I01–I05 | 5 PASS（I04 为断言写法问题，回读已证实字段持久化） |
| J 联系人/跟进 | J01–J06 | 6 PASS |
| K 线索→客户 | K01–K05 | K01/K02 有效，K03–K05 被 phase3 复跑取代 |
| N 完整客户到销售链 | N01–N08 | **8 PASS**（含 N04 反向对照） |
| O ERP 订单（修正后） | O01–O09 | 7 PASS / **1 FAIL**（O07 幂等重放 500） |
| M 输入校验探针 | M01 | **FAIL**（超长 creditCode → 500） |
| R 重启持久性 | R01–R05 | **5 PASS** |
| **合计** | | **60 项 / 56 PASS / 4 FAIL（4 项 FAIL 均为真实缺陷）** |

---

## 6. V1.1 延后清单（非阻塞，本轮不修）

| # | 项 | 性质 | 证据 |
|---|---|---|---|
| V-1 | 部署目录内 `final-verification.ps1` 是**旧副本**（17,501 B vs 仓内 27,527 B，无 `-OfflineOnly`） | 工具陈旧 | 实测哈希对比 |
| V-2 | `README.md:83-85` 旧仓 `gulierp-next` 链接 | 文档 | 靠 GitHub 重定向仍可用 |
| V-3 | `appsettings.Development.json` 已被跟踪且指向 HOLD 库 `gulierp_g2_003_test`（内容为 `CHANGE_ME`，Production 不读） | 配置风险 | blob `7cba4767…` |
| V-4 | ASP.NET Identity **角色名全局唯一**，但角色按租户作用域 ⇒ 同名角色无法跨租户复用（`--grant-sales-operator` 对第二租户报 `DuplicateRoleName`） | 架构限制 | 实测 exit 5 |
| V-5 | `--ensure-formal-enterprise-business-role-pack` 的 `Formal*Id` 常量硬编码为 `83727350616817890-95`，而**正式库实际是 30-39** ⇒ 该工具对当前正式库不可用 | 工具缺陷 | 诊断输出 `expectedIdChainMatches=false` |
| V-6 | `tools/GuliERP.Mdm.Bootstrap/ReferenceSeedCommand.cs` 的 `KnownTenantCodes`/`KnownCompanyCodes` 硬编码旧 ID（`--tenant-id`/`--company-id` 可绕过） | 潜在风险 | 源码定位；正式库编号规则实测已正确挂在 TenantId=30/CompanyId=31 |
| V-7 | `seed-mdm masterdata` / `numbering` 的 **`--dry-run` 仍然写库** | 运维安全 | 隔离库实测：dry-run 后新增 14+8+1+4 行；`reference` 子命令的 dry-run 正确回滚（对照通过） |
| V-8 | `data/bootstrap/reference/system/country.json` 等国家/区划资产 `items=0`（按设计要求运行时从权威源加载），仓内**无任何加载工具** | 缺口 | 文件自述 + 实测 0 行 |
| V-9 | CRM 商机必须经「已确认需求评估」创建，UI 之外的其它入口（如批量导入）无等价通道 | 功能缺口 | 实测 400 + 业务规则文案 |
| V-10 | 枚举以**字符串**序列化（`"Yantai"`/`"Following"`），前端枚举映射存在漂移风险（仓内已有 `CrmEnumLabelContractFacts` 守护部分映射） | 契约 | 实测响应体 |

---

## 7. 可执行上线包（**待控制者批准后执行，本轮未执行任何一步**）

### 7.1 前置证据（已具备，可直接引用）

| 证据 | 值 |
|---|---|
| 迁移安全 | 53 迁移全应用；fresh vs formal 937 列双向 0 差异；7 上下文 in-sync |
| 部署可构建 | Release publish exit 0（55 文件）+ 前端 build exit 0（69 文件） |
| 部署可运行 | Production 模式 `/health/live` 200、`/health/ready` 200 Healthy |
| 业务可用 | 60 项烟测 56 PASS；权限矩阵 11/11；客户到销售链 8/8；ERP 订单 7/8 |
| 持久性 | 重启后 5/5 可读回 |
| 回退可用 | 备份 58,409/58,409 独立复算 PASS；回退 dump 哈希吻合 |
| 代码闸门 | `dotnet test` 126/126；漂移自检 16/16；离线校验器 9/1/1（唯一 FAIL = 未推送） |

### 7.2 步骤与命令（按序，任一步失败即停并回退）

**A. 推送代码（需批准）** —— 这是「全新克隆可用」这一验收目标能成立的前提
```powershell
git -C D:\guli\projects\guli-platform push origin master
```
前置：无（当前领先 2 个提交）。回退：`git revert` 远端提交；不满足时用 `git push --force-with-lease`（**需单独批准**）。

**B. 主数据播种（需批准；注意 V-7 缺陷）**
```powershell
# 1) 字典（9 类 42 条）
dotnet tools\GuliERP.Mdm.Bootstrap\bin\Release\net10.0\seed-mdm-dictionary.dll `
  --tenant-id <PROD_TENANT_ID> --connection-string $env:ConnectionStrings__GuliERP
# 2) 编号规则（company-id 必填）
dotnet ...\seed-mdm-dictionary.dll numbering --tenant-id <PROD_TENANT_ID> --company-id <PROD_COMPANY_ID> --connection-string $env:ConnectionStrings__GuliERP
# 3) 主数据（UOM/ItemCategory/Item/BP/Warehouse/Location）
dotnet ...\seed-mdm-dictionary.dll masterdata --tenant-id <PROD_TENANT_ID> --company-id <PROD_COMPANY_ID> --connection-string $env:ConnectionStrings__GuliERP
```
⚠ **禁止加 `--dry-run`**（该标志在这两个子命令上不阻止写库，V-7）；且在当前资产下 **Item 与 BusinessPartner 会失败**（`PCS`/`KGM` 不在 UOM 目录、`CN` 不在国家参考数据）⇒ 必须先补 UOM 与国家参考数据，否则播种不完整。

**C. 国家/区划参考数据（需批准数据来源）**
按 `system/country.json` 的设计意图，从权威源（ISO 3166-1 / 国家统计局 GB/T 2260）加载并写入 `mdm.gulierp_country` / `mdm.gulierp_administrative_region`。**仓内无此工具，需先由控制者指定来源与加载方式。**

**D. 启动生产实例（需批准；当前 8080 无监听）**
```powershell
pwsh -NoProfile -File D:\guli\platform-deploy\v1.0\start-guli-platform.ps1 `
  -Database <PROD_DB> -DatabaseHost <HOST> -DatabasePort <PORT> `
  -DatabaseUser <USER> -DatabaseServerAddress <ADDR> -CredentialTarget <CRED_TARGET>
```
前置：Windows Credential Manager 中已写入目标口令（`GULIERP_DB_PASSWORD_CREDENTIAL_TARGET`）；`GULIERP_RELEASE_DB_*` 五项与连接串库名必须一致（否则 `ReleaseDatabaseTargetGuard` 拒绝启动）。
预计中断：**8080 从无监听变为有监听，秒级**；不影响 8090/8091。

**E. 部署面复验（无副作用，可随时执行）**
```powershell
pwsh -NoProfile -File D:\guli\platform-deploy\v1.0\verify-deploy-surface.ps1   # 期望 11/11 PASS
pwsh -NoProfile -File tools\dev\release-v1-acceptance\final-verification.ps1 -Repo D:\guli\projects\guli-platform -OfflineOnly
```
⚠ 部署目录内那份是**旧副本**（V-1），上线前应先同步为仓内版本，否则该脚本不认 `-OfflineOnly`、也不认 `-Repo`。

**F. 生产验收表（上线当天逐项留痕）**

| # | 检查 | 期望 | 采集方式 |
|---|---|---|---|
| F1 | `/health/live` `/health/ready` | 200 / 200 Healthy | `verify-deploy-surface.ps1` |
| F2 | 匿名访问 CRM/ERP | 401 | 同上 |
| F3 | ERP 登录 + `/auth/me` | 200 + 权限码 | HTTP 实测 |
| F4 | CRM 客户建/改/查 | 201/200/命中 | HTTP 实测 |
| F5 | 联系人 + 跟进关联 | 201 + by-company 命中 | HTTP 实测 |
| F6 | 线索→评估→转客户→商机 | 全 201/200 | HTTP 实测 |
| F7 | ERP 销售订单建/改/确认 | 201/200/200 | HTTP 实测 |
| F8 | 受限用户访问被拒 | 403 | HTTP 实测 |
| F9 | 客户/物料下拉非空 | totalCount > 0 | `/sales/orders/context/*` |
| F10 | 生产库主数据行数 | 与播种后基线一致 | 只读 SQL |

### 7.3 回退命令与恢复策略

| 场景 | 命令 | 预计中断 |
|---|---|---|
| 停止应用（回退到「无服务」） | `pwsh -NoProfile -File D:\guli\platform-deploy\v1.0\stop-guli-platform.ps1` | 秒级；**该脚本会打印回退位置**（DB 归档路径 + SHA256 + 上一部署说明） |
| 回退代码（回到 origin/master） | `git -C D:\guli\projects\guli-platform revert <commit>`（或经批准的 `--force-with-lease`） | 需重发布 + 重启，数分钟 |
| 回退数据库 | `pg_restore --no-owner --no-privileges --dbname <PROD_DB> 'D:\backup\guli-platform\guli-platform-v1-20261105T024244Z\db\guli-platform-v1-gulierp_next_prod-20261105T024244Z.dump'` | **该 dump 是 2026-11-05 的零业务快照**；恢复到它会**丢弃 11-05 之后的一切业务数据** ⇒ 执行前必须先做一份新的正式库 dump |
| 回退前留证（必做） | `pwsh -NoProfile -File tools\dev\release-v1-backup.ps1` | 分钟级 |
| 恢复演练（不碰生产） | `pwsh -NoProfile -File tools\dev\release-v1-restore-verify.ps1 -BackupRoot <BACKUP_ROOT>` | 在一次性集群里恢复并比对表清单与行数 |
| 数据库整体不可用 | 停应用 → 用最新 dump 恢复到空库 → 复跑 §7.2 F 验收表 | 取决于数据量 |

**回退前置条件（必须在批准包里写死）**：回退用的 dump 必须**晚于**本次上线时间点；当前目录里的 dump 是 11-05 的，**只能用于「回到零业务基线」，不能用于「保留上线后数据」**。

**停机与影响面**：本方案为**前台单实例**，停机窗口 = 应用进程停止到健康检查通过，实测口径 **秒级到数分钟**（取决于是否需要重发布）；IIS 站点 `Default Web Site`(80)、`GuliCrmV12Probe`(8088)、`GuliCrmV12Acceptance`(8090, Stopped)、`GuliCrmV12AlertAcceptance`(8091, Stopped) **不受本方案影响**；8090/8091 当前无监听。

---

## 8. 需控制者批准的精确下一动作（逐条，可单条批准）

1. **批准 push** `master`（2 个提交：`8281d93` 交付缺口闭合、`2ccf62d` P1-09 守卫）→ 使「全新克隆可用」验收成立。
2. **批准修改 `SalesOrderService.CreateDraftAsync`** 加幂等短路（B-3），并补一条回归测试；预计 1 处代码 + 1 个测试文件。
3. **批准为 CRM/ERP 输入加长度校验**（B-4，超长 `creditCode` → 400）。
4. **批准在生产端口启动生产实例**（B-1）并复跑 `verify-deploy-surface.ps1` 到 11/11。
5. **批准 MDM 主数据播种**（B-2），并**先行指定国家/区划参考数据的权威来源与加载方式**（V-8，仓内无工具）。
6. **批准同步部署目录内的 `final-verification.ps1` 为仓内版本**（V-1），否则上线验收脚本与仓内不一致。
7. **（可选）** 修 V-7（`seed-mdm` 的 `--dry-run` 写库）—— 若不修，则第 5 条必须在发布说明中显式写明「禁止带 `--dry-run`」。

---

## 9. 证据文件清单（均在本机，未入库）

| 文件 | 内容 |
|---|---|
| `D:/guli/isolated-v1-20261106/smoke-ledger-consolidated.json` | 60 项烟测合并台账（含每项证据串） |
| `D:/guli/isolated-v1-20261106/smoke-results*.json` | 三轮原始烟测结果 |
| `D:/guli/isolated-v1-20261106/backup-reverify-summary.json` | 备份独立复算摘要（58,409 条 / 0 失败） |
| `D:/guli/isolated-v1-20261106/backup-reverify-failures.txt` | 复算失败条目（0 行） |
| `D:/guli/isolated-v1-20261106/restart-persistence.json` | 重启持久性结果（5/5） |
| `D:/guli/isolated-v1-20261106/inv-fresh.txt` / `inv-formal.txt` | 937 列 schema 清单（fresh / formal） |
| `D:/guli/isolated-v1-20261106/deploy/logs/api.out.log` | 隔离实例运行日志（含 500 的原始异常） |
| `D:/guli/isolated-v1-20261106/smoke-operator.password` | 隔离夹具随机口令（**从未打印**；仅用于隔离库） |
