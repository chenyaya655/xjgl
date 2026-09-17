/**
 * PC 端的 `@/api/types` 兼容壳（见 ./index.ts 的说明）。
 *
 * 有些文件是按子路径导入的（`import type { X } from '@/api/types'`），
 * 因此两个子路径都要留壳。
 */
export * from '@xjgl/api'