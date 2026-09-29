/**
 * 权限码常量 —— 与后端 `MetadataPermissions` **一一对应**。
 *
 * 为什么放在共享包里而不是各端各存一份：
 *   前端要按权限码控制按钮显隐，而权限码是后端编译期常量、运行期通过
 *   `/api/app/my-profile` 返回的字符串。三端（PC / H5 / 小程序）各抄一份的话，
 *   后端加一个权限时必然出现"改了 PC 忘了 H5"，而症状是**那个按钮永远不显示、
 *   且不报任何错** —— 这类静默缺失最难排查。
 *
 * 维护约定：后端 `MetadataPermissions` 新增权限码时，这里必须同步新增。
 * 漏了不会报错，只是那个按钮永远不会显示 —— 所以后端改权限时请顺手搜这里。
 */
export const MetadataPermissions = {
  Sys: {
    Module: {
      Query: 'Sys.Module.Query',
      Create: 'Sys.Module.Create',
      Update: 'Sys.Module.Update',
      Delete: 'Sys.Module.Delete'
    },
    Permission: {
      Query: 'Sys.Permission.Query',
      Assign: 'Sys.Permission.Assign'
    },
    /**
     * 角色管理。
     *
     * ⚠ 这是全系统唯一一处「菜单权限」与「接口权限」分离的地方：
     *   菜单可见性看这几个业务权限；而接口能否调用看 ABP 内置的
     *   `AbpIdentity.Roles*` —— 角色管理页调的是框架自带的 `/api/identity/roles`。
     *   授权时**两个都要给**，只给这里会出现"菜单看得见、点进去读不到数据"。
     */
    Role: {
      Query: 'Sys.Role.Query',
      Create: 'Sys.Role.Create',
      Update: 'Sys.Role.Update',
      Delete: 'Sys.Role.Delete'
    },
    User: {
      Query: 'Sys.User.Query',
      Create: 'Sys.User.Create',
      Update: 'Sys.User.Update',
      Delete: 'Sys.User.Delete'
      // Import / Export 已撤掉：后端与界面都没有实现，留着只会让权限管理页
      // 出现"勾了却不影响任何行为"的权限项（与 MetadataPermissions.cs 保持一致）。
    },
    UserProvision: {
      Query: 'Sys.UserProvision.Query',
      Create: 'Sys.UserProvision.Create',
      Update: 'Sys.UserProvision.Update',
      Delete: 'Sys.UserProvision.Delete',
      Import: 'Sys.UserProvision.Import'
    },
    Code: {
      Query: 'Sys.Code.Query',
      Create: 'Sys.Code.Create',
      Update: 'Sys.Code.Update',
      Delete: 'Sys.Code.Delete',
      // 微代码导入导出：与组织那两条同一批，实现了才加回来
      Import: 'Sys.Code.Import',
      Export: 'Sys.Code.Export'
    },
    Org: {
      Query: 'Sys.Org.Query',
      Create: 'Sys.Org.Create',
      Update: 'Sys.Org.Update',
      Delete: 'Sys.Org.Delete',
      // 组织（学校名录）导入导出：本轮真的实现了才加回来
      Import: 'Sys.Org.Import',
      Export: 'Sys.Org.Export'
    }
  },
  Stu: {
    Student: {
      Query: 'Stu.Student.Query',
      Create: 'Stu.Student.Create',
      Update: 'Stu.Student.Update',
      Delete: 'Stu.Student.Delete',
      Import: 'Stu.Student.Import',
      Export: 'Stu.Student.Export',
      Print: 'Stu.Student.Print'
    }
  },
  Chg: {
    Type: {
      Query: 'Chg.Type.Query',
      Create: 'Chg.Type.Create',
      Update: 'Chg.Type.Update',
      Delete: 'Chg.Type.Delete'
    },
    Apply: {
      Query: 'Chg.Apply.Query',
      Create: 'Chg.Apply.Create',
      Update: 'Chg.Apply.Update',
      Delete: 'Chg.Apply.Delete',
      // 申请单导出：实现了才加回来
      Export: 'Chg.Apply.Export'
    },
    Audit: {
      Query: 'Chg.Audit.Query',
      Approve: 'Chg.Audit.Approve',
      RevokeApprove: 'Chg.Audit.RevokeApprove',
      // 审核流水导出：与申请单导出是两份不同产物，权限分开
      Export: 'Chg.Audit.Export'
    }
  },
  Pho: {
    Photo: {
      Query: 'Pho.Photo.Query',
      Upload: 'Pho.Photo.Upload',
      Delete: 'Pho.Photo.Delete',
      Export: 'Pho.Photo.Export',
      Compensate: 'Pho.Photo.Compensate'
    }
  },

  /**
   * 统计报表。
   *
   * ⚠ 刻意**不复用** Stu.Student.Query / Chg.Apply.Query：能看名单不等于能看汇总 ——
   * 汇总数字本身就是敏感信息（一个县的学生总数、异动趋势）。
   * 后端统计接口用的就是这两个码，前端按钮显隐必须跟着它们走，
   * 否则会出现"按钮点得动、接口回 403"这种最难解释的失败。
   */
  Rpt: {
    Student: {
      Query: 'Rpt.Student.Query'
    },
    Change: {
      Query: 'Rpt.Change.Query'
    }
  }
} as const
