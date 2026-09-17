import { fileURLToPath, URL } from 'node:url'

import vue from '@vitejs/plugin-vue'
import { defineConfig, loadEnv } from 'vite'

/**
 * PC 端构建配置。
 *
 * 关于跨域：开发时**不直连后端**，而是把 /api 与 /connect 代理过去。
 * 这样做有三个好处：
 *   1) 后端不需要为了开发环境放宽 CORS；
 *   2) 浏览器看到的是同源请求，不会因为「跨站」而限制某些行为；
 *   3) 生产环境前端与 API 同域（全省统一入口，nginx 同一域名下 / 与 /api），
 *      代码里始终用相对路径，**不存在「开发能跑、上线 404」的切换成本**。
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')

  // 只影响开发代理指向哪里；生产走同源，不需要配这个值
  const apiTarget = env.VITE_API_BASE_URL || 'http://localhost:44300'

  return {
    plugins: [vue()],

    resolve: {
      alias: {
        '@': fileURLToPath(new URL('./src', import.meta.url))
      }
    },

    server: {
      port: 5173,
      host: '127.0.0.1',
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true },
        '/connect': { target: apiTarget, changeOrigin: true }
      }
    },

    build: {
      outDir: 'dist',
      sourcemap: mode !== 'production',
      chunkSizeWarningLimit: 1500,
      rollupOptions: {
        output: {
          // 把体积大、更新频率低的库单独切出来，避免一个业务改动
          // 就让用户重新下载整个 vendors 包
          manualChunks: {
            vue: ['vue', 'vue-router', 'pinia'],
            element: ['element-plus', '@element-plus/icons-vue']
          }
        }
      }
    }
  }
})
