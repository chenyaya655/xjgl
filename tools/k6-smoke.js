/**
 * 冒烟 / 阶梯压测脚本（k6）。
 *
 * 对应 `deploy/README.md` 第 12 章「压测与性能基线」。
 *
 * ⚠ **BASE 请写 `127.0.0.1` 而不是 `localhost`**（在 WSL 里跑时尤其重要）。
 *   有些 WSL 发行版的 `/etc/hosts` 里同时有：
 *       127.0.0.1   localhost
 *       127.0.1.1   localhost.localdomain   localhost      ← 这一行是元凶
 *   Go 的解析器可能先取到 `127.0.1.1`，于是 k6 报
 *   `dial tcp 127.0.1.1:44300: connect: connection refused` ——
 *   而同一时刻 `curl http://localhost:...` 却是通的（curl 的解析顺序不同），
 *   非常容易误判成"后端没起来"。用 IP 就完全没有这个问题。
 *
 * 【为什么压的是这三条路径】
 * 按"最容易先垮"排序（理由见 README 第 12.1 节）：
 *   1. 学生列表 —— 最高频的读；
 *   2. 按学校统计 —— 最重的读（GROUP BY + LIMIT，数据上量后扫描面很大）；
 *   3. 当前用户上下文 —— 每次进页面都会调，且它要 join 权限/菜单/组织。
 * 写路径（照片上传）**故意不放在这里**：它同时吃 CPU、内存与 NAS，
 * 用同一个账号打满会先撞上限流（5 次/秒/用户），量出来的不是性能而是 429。
 * 要压上传请用多账号，并另写脚本（README 第 12.4 节第 3 条）。
 *
 * 【用法】
 *   # 对着**测试库**压（别对着演示/生产库）
 *   k6 run -e BASE=http://127.0.0.1:44300 -e USER=admin -e PASS='1q2w3E*' tools/k6-smoke.js
 *
 *   # 只压 100 并发、3 分钟
 *   k6 run --vus 100 --duration 3m -e BASE=http://127.0.0.1:44300 \
 *     -e USER=admin -e PASS='1q2w3E*' tools/k6-smoke.js
 *
 *   # 在 WSL 里跑（本仓库实测过的形态）
 *   wsl -e bash -lc "cd /mnt/d/StudentManagement && k6 run \
 *     -e BASE=http://127.0.0.1:44300 -e USER=admin -e PASS='1q2w3E*' tools/k6-smoke.js"
 *
 * 【阈值是"不合格线"，不是"目标线"】
 * 超了应该去加索引 / 加副本 / 查慢查询，而不是把超时调大。
 */
import http from 'k6/http'
import { check, group, sleep } from 'k6'
import { Rate } from 'k6/metrics'

const BASE = __ENV.BASE || 'http://127.0.0.1:44300'
const USER = __ENV.USER || 'admin'
const PASS = __ENV.PASS || '1q2w3E*'

/** 被限流挡下的比例。**出现 429 是预期行为**（限流在工作），
 *  但比例过高说明压测方式不对（应该用多账号），而不是系统有问题。 */
const rateLimited = new Rate('rate_limited')

export const options = {
  // 阶梯加压：目的是找到拐点，而不是一上来就打满
  stages: [
    { duration: '30s', target: 10 },
    { duration: '1m', target: 30 },
    { duration: '1m', target: 60 },
    { duration: '30s', target: 0 }
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1500', 'p(99)<4000']
  },
  // 失败时把响应体带上，便于区分"接口 500"与"业务 4xx"
  discardResponseBodies: false
}

/** 登录拿令牌。**只做一次**（setup 阶段），不要放进 default 里。 */
export function setup() {
  const res = http.post(
    `${BASE}/connect/token`,
    `grant_type=password&client_id=Xjgl_App&username=${encodeURIComponent(USER)}` +
      `&password=${encodeURIComponent(PASS)}&scope=${encodeURIComponent('Xjgl offline_access')}`,
    { headers: { 'Content-Type': 'application/x-www-form-urlencoded' } }
  )

  if (res.status !== 200) {
    throw new Error(
      `登录失败（HTTP ${res.status}）：${res.body}\n` +
        '压测前请确认账号可用、后端已启动，且 BASE 指向的是测试库。'
    )
  }

  return { token: res.json('access_token') }
}

export default function (data) {
  const auth = { headers: { Authorization: `Bearer ${data.token}` } }

  group('学生列表', () => {
    const res = http.get(
      `${BASE}/api/app/student?maxResultCount=20&sorting=${encodeURIComponent('StudentNo ASC')}`,
      auth
    )

    track429(res)
    check(res, {
      '学生列表 200': (r) => r.status === 200,
      '学生列表带分页信息': (r) => r.status !== 200 || r.json('totalCount') !== undefined
    })
  })

  group('按学校统计（最重的读）', () => {
    const res = http.get(`${BASE}/api/app/statistics/student-statistics?dimension=school`, auth)

    track429(res)
    check(res, {
      '统计 200': (r) => r.status === 200,
      // 数据范围说明必须回传：拿不到它就没法解释"为什么数字比别人小"
      '统计带数据范围说明': (r) => r.status !== 200 || r.json('scopeDescription') !== undefined
    })
  })

  group('当前用户上下文', () => {
    const res = http.get(`${BASE}/api/app/my-profile`, auth)

    track429(res)
    check(res, { '用户上下文 200': (r) => r.status === 200 })
  })

  sleep(1)
}

function track429(res) {
  rateLimited.add(res.status === 429)
}
