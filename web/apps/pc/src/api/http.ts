/**
 * PC 端的 `@/api/http` 兼容壳（见 ./index.ts 的说明）。
 *
 * 路由里注册 401 回调用的就是这里导出的 `setUnauthorizedHandler`。
 */
export * from '@xjgl/api'