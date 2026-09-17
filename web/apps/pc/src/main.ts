import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'

import 'element-plus/dist/index.css'
import '@/styles/index.css'

import App from './App.vue'
import { router } from './router'

/**
 * 应用入口。
 *
 * 顺序很重要：
 *   1) 先装 Pinia —— 路由守卫里要用 auth store，必须在挂载前就绪；
 *   2) 再装 router；
 *   3) 最后挂载。
 *
 * Element Plus 这里**整体引入**而不是按需引入：
 *   按需引入需要 unplugin-vue-components 做编译期改造，构建链更复杂，
 *   而本系统是内网/政务环境使用，首屏多几百 KB 的代价远小于
 *   「某个组件忘了引入导致线上白屏」的风险。等前端稳定后再优化。
 */
const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(ElementPlus, { locale: zhCn })

app.mount('#app')
