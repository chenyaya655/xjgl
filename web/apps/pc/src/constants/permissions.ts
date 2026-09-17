/**
 * PC 端的 `@/constants/permissions` 兼容壳。
 *
 * 权限码常量已抽到 `@xjgl/api`（PC / H5 / 小程序共用一份，避免"改一端忘一端"）。
 * 这里保留同名薄壳，让 PC 端十几处 `from '@/constants/permissions'` 一行都不用改。
 */
export * from '@xjgl/api'