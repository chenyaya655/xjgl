/**
 * 校验仓库内 markdown 的相对链接是否都能解析到真实文件。
 *
 * 【为什么需要它】文件夹里全是中文名，链接受两个坑影响：
 *   1) 手写文件名容易差一个字（`31-流程模板与实例化算法.md` 就与计划名不同）；
 *   2) 中文与空格在 markdown 链接里需要 percent-encoding，
 *      写错的表现是 GitHub 上"点进去 404"，而本地看源码完全正常。
 * 这两类错误都不会让任何测试变红，只能在发布前用脚本兜一遍。
 */
import { readFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { dirname, join, resolve, relative } from 'node:path'

const ROOT = process.cwd()
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', '.tools', 'test-results', 'playwright-report'])

function walk(dir, out = []) {
  for (const name of readdirSync(dir)) {
    if (SKIP_DIRS.has(name)) continue

    const full = join(dir, name)

    if (statSync(full).isDirectory()) walk(full, out)
    else if (name.endsWith('.md')) out.push(full)
  }

  return out
}

/**
 * 抓出 markdown 里的行内链接目标（跳过外链与锚点）。
 *
 * ⚠️ 关于查询串：GitHub 的 issue/PR 链接是**仓库相对路径 + 查询串**，例如
 *   `../../issues/new?template=bug_report.yml` —— 它指向 GitHub 的页面，
 *   不是仓库里的文件。第一版把 `?template=…` 当成文件名去 existsSync，
 *   于是报了三个假问题。所以这里要连同 `?` 一起截掉。
 */
function linkTargets(text) {
  const targets = []

  for (const m of text.matchAll(/\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)) {
    const raw = m[1]
    if (/^(https?:|mailto:|#)/i.test(raw)) continue
    targets.push(raw)
  }

  return targets
}

const files = walk(ROOT)
const problems = []
let checked = 0

for (const file of files) {
  const text = readFileSync(file, 'utf8')

  for (const raw of linkTargets(text)) {
    // 同时截掉锚点与查询串：`file.md#sec`、`../../issues/new?template=x.yml` 都不是文件路径
    const withoutAnchor = raw.split('#')[0].split('?')[0]
    if (!withoutAnchor) continue

    // GitHub 的魔法路径：`/issues/new` 由 GitHub 自己处理，仓库里不存在这个目录。
    // 不豁免的话每次都会报"目标不存在"，久了就没人看这个脚本了。
    if (/(^|\/)issues\/new$/.test(withoutAnchor)) continue

    let decoded
    try {
      decoded = decodeURIComponent(withoutAnchor)
    } catch {
      problems.push({ file, raw, why: 'percent-encoding 不合法' })
      continue
    }

    const abs = resolve(dirname(file), decoded)
    checked++

    if (!existsSync(abs)) {
      // 给出"最接近的候选"，让修复不用去翻目录
      const dir = dirname(abs)
      let hint = ''

      if (existsSync(dir)) {
        const prefix = decoded.slice(decoded.lastIndexOf('/') + 1, decoded.lastIndexOf('/') + 4)
        const near = readdirSync(dir)
          .filter((n) => n.startsWith(prefix.slice(0, 2)))
          .slice(0, 3)

        if (near.length) hint = ` 候选: ${near.join(' | ')}`
      }

      problems.push({
        file: relative(ROOT, file),
        raw,
        why: `目标不存在: ${relative(ROOT, abs)}${hint}`
      })
    }
  }
}

console.log(`检查 ${files.length} 个 markdown、${checked} 个相对链接`)

if (problems.length === 0) {
  console.log('✅ 全部相对链接可解析')
} else {
  console.log(`\n❌ ${problems.length} 个链接有问题：`)
  for (const p of problems) {
    console.log(`  ${p.file}\n     ${p.raw}\n     ${p.why}`)
  }
  process.exitCode = 1
}
