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
    User: {
      Query: 'Sys.User.Query',
      Create: 'Sys.User.Create',
      Update: 'Sys.User.Update',
      Delete: 'Sys.User.Delete',
      Import: 'Sys.User.Import',
      Export: 'Sys.User.Export'
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
      Import: 'Sys.Code.Import',
      Export: 'Sys.Code.Export'
    },
    Org: {
      Query: 'Sys.Org.Query',
      Create: 'Sys.Org.Create',
      Update: 'Sys.Org.Update',
      Delete: 'Sys.Org.Delete',
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
      Export: 'Chg.Apply.Export'
    },
    Audit: {
      Query: 'Chg.Audit.Query',
      Approve: 'Chg.Audit.Approve',
      RevokeApprove: 'Chg.Audit.RevokeApprove',
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
  }
} as const
