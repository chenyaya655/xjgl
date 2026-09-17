import { fileURLToPath, URL } from 'node:url'

import vue from '@vitejs/plugin-vue'
import { defineConfig, loadEnv } from 'vite'

/**
 * H5（手机端）构建配置。
 *
 * 与 PC 端的三处不同，都是移动端的实际约束决定的：
 *
 * 1) **端口 5174**：与 PC 端（5173）分开，后端 appsettings 里的 CorsOrigins
 *    本来就配了 5173/5174，因此两边可以同时跑，不需要改后端配置。
 *
 * 2) **`base: './'`（相对路径）**：政务系统的 H5 经常挂在子路径下
 *    （例如 https://xxx.gov.cn/xjgl/h5/），用绝对路径 `/assets/...` 会 404。
 *
 * 3) **构建目标调低到 es2015**：教师的手机型号很杂，仍有 Android 7/8 的老机器。
 *    默认的 esnext 目标会让老 WebView 直接白屏 —— 这类问题在开发机上永远看不到。
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const apiTarget = env.VITE_API_BASE_URL || 'http://localhost:44300'

  return {
    plugins: [vue()],

    base: './',

    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url))
      }
    },

    server: {
      port: 5174,
      host: '127.0.0.1',
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true },
        '/connect': { target: apiTarget, changeOrigin: true }
      }
    },

    build: {
      outDir: 'dist',
      target: 'es2015',
      sourcemap: mode !== 'production',
      rollupOptions: {
        output: {
          // 与 PC 端一样把大库切出来，但移动端**更需要**这件事：
          // 不切分时整个应用是一个 1.4MB 的 index.js，教师在农村 4G 上
          // 首屏要等好几秒。切分后 vue 与 element-plus 可以长期缓存，
          // 业务代码改动只让用户重下几十 KB。
          //
          // 注意这**没有**解决首屏体积本身 —— 完整的 Element Plus 约 1MB。
          // 真要压下去得改成按需引入（unplugin-vue-components），
          // 那会动构建链，留作后续优化（已记在 web/README 的待办里）。
          manualChunks: {
            vue: ['vue', 'vue-router', 'pinia'],
            element: ['element-plus', '@element-plus/icons-vue']
          }
        }
      }
    }
  }
})
