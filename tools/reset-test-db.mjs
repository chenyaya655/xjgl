#!/usr/bin/env node
/**
 * 重建**测试库**（ROADMAP 第 13 条 / 技术债 8）。
 *
 * 【为什么需要它】
 * 集成测试与端到端测试都打真实 PostgreSQL，而用例**不清库**：
 *   1) 用例会造组织、模块、码项、角色…… 跑得越久，库越大；
 *   2) 依赖"库里已有数据"的断言会慢慢失真（实测曾累积到 4000+ 个测试组织、
 *      1.3 万所学校，"模块数"这类断言逐渐失去意义）；
 *   3) 一些偶发失败（编码撞号、唯一约束冲突）本质上是**残留数据导致的**，
 *      在干净库上根本复现不出来 —— 于是排查方向会被带偏。
 *
 * CI 每轮都是干净库（job 里的 postgres service 是新的），所以这个问题
 * **只在本机出现**。这个脚本就是那条"一条命令得到干净测试库"的路。
 *
 * 【安全设计：只肯动"看起来是测试库"的名字】
 * 它会 `DROP DATABASE`。所以库名必须匹配 /test/i，否则直接拒绝 ——
 * 宁可让你改库名，也不要出现"手滑把开发库/演示库删了"。
 * 想对别的库执行，得显式加 `--allow-any-database`（并自己承担后果）。
 *
 * 【用法】
 *   node tools/reset-test-db.mjs            # 只看会做什么（dry-run，默认）
 *   node tools/reset-test-db.mjs --yes      # 真的执行
 *   node tools/reset-test-db.mjs --yes --connection "Host=...;Database=xjgl_test;..."
 *
 * 连接串取值顺序：--connection > 环境变量 XJGL_TEST_CONNECTION >
 * 与 test/Xjgl.TestBase/XjglTestConfiguration.cs 一致的默认值
 * （本机 127.0.0.1:55432 / 库 xjgl_test / 用户 xjgl）。
 *
 * 执行完库是**空的**，需要再跑一次迁移器建表与种子数据：
 *   dotnet run --project src/Xjgl.DbMigrator
 */

import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(here, '..')

/** 与 XjglTestConfiguration.DefaultConnectionString 保持一致。 */
const DEFAULT_CONNECTION =
  'Host=127.0.0.1;Port=55432;Database=xjgl_test;User ID=xjgl;Password=Xjgl@Dev2026'

const args = process.argv.slice(2)

function argValue(name) {
  const i = args.indexOf(name)

  return i >= 0 ? args[i + 1] : undefined
}

const dryRun = !args.includes('--yes')
const allowAnyDatabase = args.includes('--allow-any-database')
/** 连数据库一起重建（默认只重建 schema，见 main() 里的说明）。 */
const dropDatabase = args.includes('--drop-database')
const connectionString = argValue('--connection') ?? process.env.XJGL_TEST_CONNECTION ?? DEFAULT_CONNECTION

/** 解析 Npgsql 风格的连接串（`Key=Value;...`，键名大小写与空格不敏感）。 */
function parseConnectionString(cs) {
  const map = {}

  for (const segment of cs.split(';')) {
    const trimmed = segment.trim()

    if (!trimmed) {
      continue
    }

    const eq = trimmed.indexOf('=')

    if (eq < 0) {
      continue
    }

    // "User ID" / "UserID" / "Username" 都归一到 user
    const key = trimmed.slice(0, eq).trim().toLowerCase().replace(/\s+/g, '')
    map[key] = trimmed.slice(eq + 1).trim()
  }

  return {
    host: map.host ?? '127.0.0.1',
    port: map.port ?? '5432',
    database: map.database ?? '',
    user: map.userid ?? map.username ?? map.user ?? 'postgres',
    password: map.password ?? ''
  }
}

/**
 * 找到 psql。
 *
 * 优先 `--psql` / 环境变量 PSQL，其次 PATH，最后本仓库沙箱里的自带版本
 * （`.tools/pgsql-extract/`，见 AGENTS 类的环境说明）。
 * 不引 npm 依赖：这个脚本要能在"只装了数据库客户端"的机器上直接跑。
 */
function findPsql() {
  const explicit = argValue('--psql') ?? process.env.PSQL

  if (explicit && fs.existsSync(explicit)) {
    return explicit
  }

  const onPath = spawnSync('psql', ['--version'], { encoding: 'utf8' })

  if (onPath.status === 0) {
    return 'psql'
  }

  const candidates = [
    path.join(repoRoot, '.tools', 'pgsql-extract', 'pgsql', 'bin', 'psql.exe'),
    path.join(repoRoot, '.tools', 'pgsql-extract', 'pgsql', 'bin', 'psql')
  ]

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) {
      return candidate
    }
  }

  return null
}

/**
 * 执行一条 SQL。
 *
 * 【为什么用 `stdio: 'inherit'` 而不是默认的管道】
 * 默认（`encoding: 'utf8'`）会让 Node 用**管道**收集 psql 的输出，
 * 而受限环境（本仓库的沙箱）禁止打开管道 —— 表现为 `spawnSync` 返回
 * `status: null`，报错信息是"退出码 null"，看上去像 psql 崩了，实际根本没启动起来。
 * 改成继承父进程的 stdio 就完全不需要管道：输出直接进终端（人也能看到进度）。
 * 需要**读回结果**时（判断目标库是否存在）用 psql 的 `--output` 写到临时文件再读，
 * 同样绕开管道。
 *
 * 这个坑是实测踩到的：dry-run 一路正常（它不调 psql），
 * 于是 `--yes` 真执行那条路从未被验证过。
 */
function run(psql, conn, database, sql, { capture = false } = {}) {
  const args = [
    '--host', conn.host,
    '--port', String(conn.port),
    '--username', conn.user,
    // 连到维护库（postgres）才能 DROP 目标库
    '--dbname', database,
    '--no-psqlrc',
    '--set', 'ON_ERROR_STOP=1',
    '--command', sql
  ]

  let outFile = null

  if (capture) {
    outFile = path.join(os.tmpdir(), `xjgl-reset-${process.pid}-${Date.now()}.out`)
    args.push('--output', outFile)
  }

  const result = spawnSync(psql, args, {
    stdio: 'inherit',
    env: { ...process.env, PGPASSWORD: conn.password }
  })

  if (result.status !== 0) {
    throw new Error(
      `psql 执行失败（退出码 ${result.status ?? '未启动'}）：${sql}\n` +
        (result.status === null
          ? '  「未启动」通常是受限环境禁止创建管道/子进程所致，与数据库无关。'
          : '')
    )
  }

  if (!capture) {
    return ''
  }

  const text = fs.readFileSync(outFile, 'utf8')
  fs.unlinkSync(outFile)

  return text
}

function main() {
  const conn = parseConnectionString(connectionString)

  if (!conn.database) {
    console.error('❌ 连接串里没有 Database=，无法判断要重建哪个库。')
    process.exit(1)
  }

  const looksLikeTestDb = /test/i.test(conn.database)

  if (!looksLikeTestDb && !allowAnyDatabase) {
    console.error(
      `❌ 库名 "${conn.database}" 看起来不是测试库，拒绝执行（这个脚本会 DROP DATABASE）。\n` +
        '   如果确实要重建它，显式加 --allow-any-database。'
    )
    process.exit(1)
  }

  const psql = findPsql()

  if (!psql) {
    console.error(
      '❌ 找不到 psql。请安装 PostgreSQL 客户端，或用 --psql <路径> / 环境变量 PSQL 指定。'
    )
    process.exit(1)
  }

  console.log('将要清空的对象：')
  console.log(`  psql      : ${psql}`)
  console.log(`  地址       : ${conn.host}:${conn.port}`)
  console.log(`  库         : ${conn.database}`)
  console.log(`  用户       : ${conn.user}`)
  console.log(`  方式       : ${dropDatabase ? 'DROP DATABASE 后重建（重）' : 'DROP SCHEMA public 后重建（默认，轻）'}`)
  console.log('')

  // 默认用「重建 schema」而不是「重建数据库」。
  //
  // 【为什么不是 DROP DATABASE】
  //   1. DROP DATABASE 会强制触发一次 **checkpoint**（并在需要时向其它进程发信号踢连接），
  //      在不允许进程间信号的环境里会直接失败：`could not signal for checkpoint: Operation not permitted`。
  //      更糟的是它可能**只做了一半** —— 目录被删掉、catalog 里留下一个 "invalid database"，
  //      此后连不上、也不能再 CREATE 同名库。本轮实测就这么踩过一次。
  //   2. "干净的测试库"这个目标，DROP SCHEMA public CASCADE 就完全达到了：
  //      所有表/序列/扩展对象都在 public 里，清掉再建 = 与全新库等价，
  //      而迁移器会把 EF 迁移表与业务表全部重建。
  //   3. 它不会踢掉同库上的其它会话，所以也不会误伤正在跑的任务。
  //
  // 真要连数据库一起重来（例如想换所有者/编码），加 `--drop-database` 显式选择。
  if (dryRun) {
    console.log('（dry-run）实际会执行：')

    if (dropDatabase) {
      console.log(`  DROP DATABASE IF EXISTS "${conn.database}" WITH (FORCE);`)
      console.log(`  CREATE DATABASE "${conn.database}" ENCODING 'UTF8';`)
      console.log('  然后重新创建 pg_trgm / "uuid-ossp" / unaccent 三个扩展')
    } else {
      console.log('  DROP SCHEMA IF EXISTS public CASCADE;')
      console.log('  CREATE SCHEMA public;')
      console.log('  CREATE EXTENSION IF NOT EXISTS pg_trgm / "uuid-ossp" / unaccent;')
    }

    console.log('')
    console.log('确认无误后加 --yes 真正执行。')
    return
  }

  if (dropDatabase) {
    // WITH (FORCE) 是 PG 13+ 的能力：把还连着的会话踢掉。
    run(psql, conn, 'postgres', `DROP DATABASE IF EXISTS "${conn.database}" WITH (FORCE);`)
    run(psql, conn, 'postgres', `CREATE DATABASE "${conn.database}" ENCODING 'UTF8';`)
  } else {
    run(psql, conn, conn.database, 'DROP SCHEMA IF EXISTS public CASCADE;')
    run(psql, conn, conn.database, 'CREATE SCHEMA public;')
  }

  // 扩展要重建：它们默认装在 public 里，schema 被 drop 就一起没了，
  // 而应用依赖 pg_trgm（模糊检索）与 uuid-ossp（UUID 生成）。
  // `pg_stat_statements` 需要 shared_preload_libraries，测试库不强求，失败只提示不中断。
  for (const extension of ['pg_trgm', '"uuid-ossp"', 'unaccent']) {
    try {
      run(psql, conn, conn.database, `CREATE EXTENSION IF NOT EXISTS ${extension};`)
    } catch (error) {
      console.warn(`  ⚠ 扩展 ${extension} 创建失败（应用不一定会用到，可忽略）：${error.message}`)
    }
  }

  console.log('')
  console.log(`✅ ${conn.database} 已清空 —— 跑测试前**必须**先跑一次迁移器建表与种子：`)
  console.log('')
  console.log('   dotnet run --project src/Xjgl.DbMigrator')
  console.log('')
  console.log('（迁移器是幂等的；测试库与演示库是两个库，别只跑一个。）')
}

main()
