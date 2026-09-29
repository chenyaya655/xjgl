/**
 * CSV 编码探测（债 10）的单元测试。
 *
 * 【为什么用 node:test 而不是引 vitest】
 * 被测代码是**纯函数、零依赖、零 DOM**，为一个百来行的工具引一整套测试运行器+
 * 变换器（vitest 至少要拉 vite/esbuild 一串依赖）不划算。
 * Node 22.7+ 能直接跑 TypeScript（仅做类型擦除），配上内置的 `node:test`
 * 就够用，且**新增依赖为零** —— 这也是它能进 CI 而不会成为维护负担的原因。
 *
 * 运行：`node --test web/apps/pc/src/utils/csvEncoding.test.ts`
 *   （在 `web/` 目录下执行；package.json 里的 `test:unit` 已封装）
 *
 * 【这组用例守什么】
 * 编码探测是"宁可拒绝也不能猜错"的地方：猜错会把**错误的姓名**静默写进学籍主档，
 * 而姓名要等到打印学籍卡才会被人发现。所以正例（必须放行）与反例（必须拒绝）
 * 都要钉死，尤其是"GBK 必须被识别为 GBK 并给出可执行的建议"。
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'

import { probeCsvEncoding } from './csvEncoding.ts'

/** 把字节数组换成 ArrayBuffer（探测函数的入参口径）。 */
function buf(bytes: number[]): ArrayBuffer {
  return new Uint8Array(bytes).buffer
}

/** GBK 字节字面量 → ArrayBuffer，便于把"预期的原始字节"写在用例里。 */
function gbk(bytes: number[]): ArrayBuffer {
  return buf(bytes)
}

const utf8 = (text: string): ArrayBuffer => {
  const encoded = new TextEncoder().encode(text)

  return encoded.buffer.slice(encoded.byteOffset, encoded.byteOffset + encoded.byteLength) as ArrayBuffer
}

// ---------------------------------------------------------------------------
// 必须放行
// ---------------------------------------------------------------------------

test('纯 ASCII（无 BOM）按 UTF-8 放行', () => {
  const probe = probeCsvEncoding(utf8('name,gender\nZhang,1\n'))

  assert.equal(probe.ok, true)
  assert.equal(probe.detected, 'UTF-8（无 BOM）')
  assert.match(probe.text, /^name,gender/)
})

test('UTF-8 无 BOM 的中文按 UTF-8 放行', () => {
  const probe = probeCsvEncoding(utf8('姓名,性别\n张三,男\n'))

  assert.equal(probe.ok, true)
  assert.ok(probe.text.includes('姓名,性别'), `实际：${probe.detected}`)
  assert.ok(probe.text.includes('张三,男'))
})

test('UTF-8 BOM 被剥掉，且表头第一个列名不带 BOM 字符', () => {
  // BOM 留在第一列列名里会让"姓名"匹配不上，表现为「缺少必需列：姓名」
  const withBom = new Uint8Array([0xef, 0xbb, 0xbf, ...new Uint8Array(utf8('姓名,性别\n'))])

  const probe = probeCsvEncoding(withBom.buffer as ArrayBuffer)

  assert.equal(probe.ok, true)
  assert.equal(probe.detected, 'UTF-8（带 BOM）')
  assert.ok(
    probe.text.startsWith('姓名'),
    `BOM 必须被剥掉，实际开头：${JSON.stringify(probe.text.slice(0, 3))}`
  )
  assert.equal(probe.text.charCodeAt(0), 0x59d3, '第一个字符应当是「姓」而不是 U+FEFF')
})

test('空文件放行（"文件为空"由后端统一报，前端不重复实现那条规则）', () => {
  const probe = probeCsvEncoding(buf([]))

  assert.equal(probe.ok, true)
  assert.equal(probe.text, '')
})

// ---------------------------------------------------------------------------
// 必须拒绝
// ---------------------------------------------------------------------------

test('GBK 编码的文件被拒绝，并明确指认为 GBK', () => {
  // 「姓名,性别\n张三,男」的 GBK 字节
  const probe = probeCsvEncoding(
    gbk([
      0xd0, 0xd5, 0xc3, 0xfb, 0x2c, 0xd0, 0xd4, 0xb1, 0xf0, 0x0a,
      0xd5, 0xc5, 0xc8, 0xfd, 0x2c, 0xc4, 0xd0
    ])
  )

  assert.equal(probe.ok, false, 'GBK 中文绝不能当成 UTF-8 放行（会写入错误的姓名）')
  assert.equal(probe.detected, 'GBK / GB18030（本地编码）')
  assert.equal(probe.text, '', '被拒绝时不能给出可能被误用的文本')
  assert.ok(probe.reason, '必须给出原因')
  assert.ok(
    probe.reason!.includes('CSV UTF-8'),
    `建议必须可执行（告诉用户怎么另存），实际：${probe.reason}`
  )
})

test('UTF-16 LE（Excel「Unicode 文本」另存）被拒绝并说明原因', () => {
  const utf16 = new Uint8Array([0xff, 0xfe])
  const body = utf8('姓名,性别\n')

  // UTF-16LE 的「姓」是 D3 59
  utf16[utf16.length] = 0xd3
  utf16[utf16.length] = 0x59
  utf16[utf16.length] = 0x0d
  utf16[utf16.length] = 0x54

  const probe = probeCsvEncoding(utf16.buffer as ArrayBuffer)

  void body

  assert.equal(probe.ok, false)
  assert.equal(probe.detected, 'UTF-16 LE')
  assert.ok(probe.reason!.includes('UTF-16'))
})

test('截断的多字节序列被拒绝（文件被切断，或根本不是 UTF-8）', () => {
  // 「姓」的 UTF-8 是 E5 A7 93，这里故意只给前两个字节
  const probe = probeCsvEncoding(buf([0xe5, 0xa7]))

  assert.equal(probe.ok, false)
  assert.ok(probe.reason!.includes('第 1 个字节处不合法'), `实际：${probe.reason}`)
})

test('过长编码（0xC0 0x80）被拒绝 —— RFC 3629 明确禁止', () => {
  const probe = probeCsvEncoding(buf([0xc0, 0x80]))

  assert.equal(probe.ok, false, '0xC0/0xC1 不可能出现在合法 UTF-8 里')
})

test('UTF-8 编码的代理区码点（0xED 0xA0 0x80）被拒绝', () => {
  // U+D800 的"UTF-8 编码"是非法序列，必须拒绝（否则会解出半个代理对）
  const probe = probeCsvEncoding(buf([0xed, 0xa0, 0x80]))

  assert.equal(probe.ok, false)
})

test('高位首字节 0xF5~0xFF 被拒绝（超出 U+10FFFF）', () => {
  assert.equal(probeCsvEncoding(buf([0xf5, 0x80, 0x80, 0x80])).ok, false)
  assert.equal(probeCsvEncoding(buf([0xff])).ok, false)
})

// ---------------------------------------------------------------------------
// 边界：不能把合法的 4 字节汉字误判
// ---------------------------------------------------------------------------

test('4 字节 UTF-8（生僻字 U+20BB7 𠮷）正常放行', () => {
  // 「𠮷」= F0 A0 AE B7，是 4 字节序列里最容易被"过长编码"规则误伤的一类
  const probe = probeCsvEncoding(buf([0xf0, 0xa0, 0xae, 0xb7]))

  assert.equal(probe.ok, true, '生僻字必须能导入 —— 本系统专门为它做了整条链路')
  assert.equal(probe.text, '𠮷')
  assert.equal(probe.text.codePointAt(0), 0x20bb7)
})
