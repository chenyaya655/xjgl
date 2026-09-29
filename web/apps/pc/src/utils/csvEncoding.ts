/**
 * CSV 文件的编码探测（债 10）。
 *
 * 【为什么必须有它】
 * 导入接口只接受 UTF-8。而老师在 Excel 里"另存为 CSV"时，默认是**本机编码**
 * （简体中文 Windows 上是 GBK/GB18030）。那种文件用 `File.text()` 读会得到一串
 * U+FFFD 替换字符，于是：
 *   - 表头「姓名」变成「??」→ 后端报「导入文件缺少必需列：姓名、性别…」；
 *   - 用户看到的提示与真正的原因（编码不对）毫无关系，
 *     于是他会反复检查列名、重排列、甚至重装 Office —— 问题永远解决不了。
 *
 * 所以读文件之前先按**字节序**判一次编码，非 UTF-8 时直接告诉他
 * 「请用 UTF-8 编码另存」，而不是把乱码送去后端换回一句莫名其妙的报错。
 *
 * 【为什么是"探测"而不是"自动转码"】
 * 把 GBK 自动转成 UTF-8 需要完整的 GBK 码表（几百 KB 的表 + 引一个库）。
 * 而且"猜错编码"比"明确拒绝"更糟：猜错会静默产生**错误的姓名**入库，
 * 而姓名错了要发现时已经打印过学籍卡了。明确要求 UTF-8 是一次性成本，
 * 且模板本身带 BOM，用 Excel 打开再保存仍是 UTF-8。
 */

/** 探测结果。 */
export interface CsvEncodingProbe {
  /** 能不能安全地按 UTF-8 交给后端。 */
  ok: boolean
  /** 人类可读的编码判定，用于提示语与排查。 */
  detected: string
  /** 检测到的文本（已剥掉 BOM）；`ok` 为 false 时为空串。 */
  text: string
  /** `ok` 为 false 时给出的、直接可执行的处理建议。 */
  reason?: string
}

/** UTF-8 BOM。注意是**字节** EF BB BF，不是字符串里的 \uFEFF。 */
const UTF8_BOM = [0xef, 0xbb, 0xbf]

/** UTF-16 的 BOM（大小端）。Excel 的"Unicode 文本"另存是 UTF-16LE。 */
const UTF16_LE_BOM = [0xff, 0xfe]
const UTF16_BE_BOM = [0xfe, 0xff]

function startsWith(bytes: Uint8Array, prefix: number[]): boolean {
  if (bytes.length < prefix.length) {
    return false
  }

  return prefix.every((b, i) => bytes[i] === b)
}

/**
 * 严格按 RFC 3629 校验一段字节是否是合法 UTF-8。
 *
 * 【为什么不图省事用 `TextDecoder` + 找 U+FFFD】
 * `TextDecoder('utf-8', { fatal: true })` 也能判非法，但它的报错是
 * "The encoded data was not valid for encoding utf-8"，没法定位到第几个字节；
 * 而"第 N 行的第 M 个字节不合法"对排查（例如文件其实是半截的 GBK）非常有用。
 * 另外自己走一遍状态机，才能顺手把"是不是 GBK"这个判断做得像样一点
 * （GBK 的双字节首字节落在 0x81~0xFE，与 UTF-8 的续字节规则冲突，很容易识别）。
 */
function validateUtf8(bytes: Uint8Array): { valid: boolean; badIndex: number } {
  let i = 0

  while (i < bytes.length) {
    const b0 = bytes[i]

    // 单字节：0x00~0x7F
    if (b0 <= 0x7f) {
      i += 1
      continue
    }

    let need = 0
    let min = 0
    let codePoint = 0

    if (b0 >= 0xc2 && b0 <= 0xdf) {
      // 2 字节；0xC0/0xC1 是"过长编码"，按 RFC 3629 一律非法
      need = 1
      min = 0x80
      codePoint = b0 & 0x1f
    } else if (b0 >= 0xe0 && b0 <= 0xef) {
      need = 2
      min = 0x800
      codePoint = b0 & 0x0f
    } else if (b0 >= 0xf0 && b0 <= 0xf4) {
      need = 3
      min = 0x10000
      codePoint = b0 & 0x07
    } else {
      // 0x80~0xC1 与 0xF5~0xFF 都不可能是 UTF-8 的首字节
      return { valid: false, badIndex: i }
    }

    if (i + need >= bytes.length) {
      // 截断的多字节序列（文件被截过，或根本不是 UTF-8）
      return { valid: false, badIndex: i }
    }

    for (let k = 1; k <= need; k += 1) {
      const bk = bytes[i + k]

      if ((bk & 0xc0) !== 0x80) {
        return { valid: false, badIndex: i + k }
      }

      codePoint = (codePoint << 6) | (bk & 0x3f)
    }

    // 过长编码与代理区（U+D800~U+DFFF）在 UTF-8 里都是非法的
    if (codePoint < min || (codePoint >= 0xd800 && codePoint <= 0xdfff) || codePoint > 0x10ffff) {
      return { valid: false, badIndex: i }
    }

    i += need + 1
  }

  return { valid: true, badIndex: -1 }
}

/** 粗略判断是不是 GBK/GB18030：看有没有落在 GBK 双字节首字节区间的高字节。 */
function looksLikeGbk(bytes: Uint8Array): boolean {
  let gbkPairs = 0

  for (let i = 0; i < bytes.length; i += 1) {
    const b = bytes[i]

    if (b >= 0x81 && b <= 0xfe) {
      const next = bytes[i + 1]

      // GBK 第二字节：0x40~0xFE（去掉 0x7F）
      if (next !== undefined && next >= 0x40 && next <= 0xfe && next !== 0x7f) {
        gbkPairs += 1
        i += 1
      }
    }
  }

  // 出现若干组"像 GBK 的双字节"就认定是 GBK —— 这个判断只需要支撑提示语，
  // 不需要严谨到能分辨 GBK 与 Big5。
  return gbkPairs >= 2
}

/**
 * 探测一段内容是否可以作为 UTF-8 CSV 交给后端。
 *
 * 接受的三种情况（都是 UTF-8 或其兼容子集）：
 *   1. 带 UTF-8 BOM；
 *   2. 不带 BOM 但整段字节是合法 UTF-8（纯 ASCII 也走这一条）；
 *   3. 空文件 —— 交给后端去报"文件为空"，前端不重复实现那条规则。
 */
export function probeCsvEncoding(buffer: ArrayBuffer): CsvEncodingProbe {
  const bytes = new Uint8Array(buffer)

  if (bytes.length === 0) {
    return { ok: true, detected: '空文件', text: '' }
  }

  if (startsWith(bytes, UTF8_BOM)) {
    // 剥掉 BOM 再解码：BOM 留在第一列的列名里会让"姓名"匹配不上，
    // 表现为「缺少必需列：姓名」—— 而后端其实已经容忍 BOM，
    // 这里剥掉只是为了让前端的"列名提示"与后端一致。
    const body = bytes.subarray(UTF8_BOM.length)

    return { ok: true, detected: 'UTF-8（带 BOM）', text: new TextDecoder('utf-8').decode(body) }
  }

  if (startsWith(bytes, UTF16_LE_BOM) || startsWith(bytes, UTF16_BE_BOM)) {
    const little = startsWith(bytes, UTF16_LE_BOM)

    return {
      ok: false,
      detected: little ? 'UTF-16 LE' : 'UTF-16 BE',
      text: '',
      reason:
        'Excel 的「Unicode 文本」另存是 UTF-16，本系统导入只接受 UTF-8。' +
        '请改用「CSV UTF-8（逗号分隔）」另存，或先下载本页模板再填写。'
    }
  }

  const check = validateUtf8(bytes)

  if (check.valid) {
    return { ok: true, detected: 'UTF-8（无 BOM）', text: new TextDecoder('utf-8').decode(bytes) }
  }

  const gbk = looksLikeGbk(bytes)

  return {
    ok: false,
    detected: gbk ? 'GBK / GB18030（本地编码）' : '未知的非 UTF-8 编码',
    text: '',
    reason:
      `文件不是 UTF-8 编码（判定：${gbk ? 'GBK/GB18030' : '未知'}，` +
      `第 ${check.badIndex + 1} 个字节处不合法）。` +
      'Excel 的「另存为 CSV」默认使用本机编码，中文会变成乱码，' +
      '导入时只会看到「缺少必需列」，看不出真正原因。' +
      '请在 Excel 里选择「CSV UTF-8（逗号分隔）」另存后再导入。'
  }
}
