import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'

import 'element-plus/dist/index.css'
import '@/styles/index.css'

import App from './App.vue'
import { router } from './router'

/**
 * H5 应用入口。
 *
 * 组件库沿用 Element Plus（而不是移动端更常见的 Vant），这是一个**有意识的取舍**：
 *   - 收益：PC 与 H5 共用一套组件 API 与一套使用经验，界面代码可以互相参照；
 *     本项目 H5 的场景很窄（待办审核 + 拍照上传 + 我的），界面复杂度低，
 *     并不需要 Vant 那些移动端专属组件；
 *   - 代价：Element Plus 的表单/表格在手机上偏"桌面感"，触摸体验不如 Vant。
 *     如果将来 H5 要做成面向家长的高频应用，应当换成 Vant。
 *
 * 这个判断写在代码里而不是只写在文档里 —— 后来的人看到这个选择时，
 * 应该能立刻知道它是权衡的结果，而不是"随手拿了一个组件库"。
 */
const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(ElementPlus, { locale: zhCn, size: 'default' })

app.mount('#app')
