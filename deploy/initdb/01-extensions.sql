-- ===========================================================================
--  学籍管理系统 —— 数据库初始化脚本
--  由 docker-entrypoint-initdb.d 在容器首次启动（数据卷为空）时自动执行。
--  注意：只在初始化时跑一次；后续修改请写成迁移脚本。
-- ===========================================================================

-- 性能观测：慢 SQL 统计
CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

-- 姓名模糊检索（生僻字按原样匹配，不依赖拼音）
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- UUID 生成
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- 去音标/变音符号，便于姓名与地名的宽松匹配
CREATE EXTENSION IF NOT EXISTS unaccent;

-- ---------------------------------------------------------------------------
-- 中文 ICU 排序规则
--   PostgreSQL 内置的 zh-x-icu 可用于按中文习惯排序；
--   这里再建一个显式命名的排序规则，业务表可按需引用：
--       ORDER BY student_name COLLATE zh_icu
--   注意：生僻字（CJK 扩展 B 区及以上）在 ICU 里的排序位置由 ICU 版本决定，
--   因此学籍系统列表默认按“学校 + 年级班级 + 学籍辅号”排序，不按姓名排序。
-- ---------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_collation WHERE collname = 'zh_icu') THEN
        CREATE COLLATION zh_icu (provider = icu, locale = 'zh-CN');
    END IF;
END
$$;

-- 确认扩展已就绪（输出会出现在 docker logs 里）
SELECT extname, extversion FROM pg_extension ORDER BY extname;
