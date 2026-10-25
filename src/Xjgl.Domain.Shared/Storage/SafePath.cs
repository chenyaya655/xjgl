namespace Xjgl.Storage;

/// <summary>
/// 把「相对路径」安全地解析到某个根目录之下。
///
/// 【为什么单独抽出来】
///   照片存储、导出产物存储、异动附件存储三处各写了一份 `ResolvePath`，
///   三份**代码完全一样**（连注释都互相指着对方）。三份一样的守卫有两个风险：
///     1. 修一处、漏两处 —— 本仓库在数据范围那件事上已经栽过一次
///        （"原先 3 处各写一份私有常量，漏掉的那一处就是它"）；
///     2. 真实缺陷会被"我们已经有校验"的印象盖住。
///   抽成一处之后，边界只有一份实现、一份测试。
///
/// 【★★ 这里修掉的是一个真实的路径穿越漏洞】
///   原来的写法是：
///     <code>
///     var combined = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
///     if (!combined.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)) throw ...
///     </code>
///   而 `_rootPath` 是 `Path.GetFullPath(root)`，**不带结尾分隔符**。
///   于是"前缀相同"不等于"在里面"：
///     根目录 <c>/data/photos</c> + 相对路径 <c>../photos-evil/x.jpg</c>
///     → 解析成 <c>/data/photos-evil/x.jpg</c>
///     → 它确实以 <c>/data/photos</c> 开头 → **校验通过，但已经跑到根目录外面去了**。
///   只要目标目录的名字是根目录名的前缀（`photos` / `photos-evil`、
///   `xjgl-export` / `xjgl-export-backup` —— 运维建目录时非常常见的命名），
///   这个洞就能用，而且**不需要任何异常**：读写都"成功"。
///   修法是把根目录补上结尾分隔符再比前缀，并单独放行"等于根目录本身"这一种情况。
///
/// 【为什么大小写比较要分平台】
///   Windows 的路径不区分大小写，Linux 区分。用 `OrdinalIgnoreCase` 去比 Linux 路径
///   会让 <c>/data/Photos</c> 被当成 <c>/data/photos</c> 的子孙 —— 对一个**安全判断**
///   来说，任何"比实际更宽松"的比较都不该出现在这里。
///
/// 【它挡不住什么（如实写在代码里）】
///   这里只挡"字符串层面解析后跑出根目录"。符号链接（symlink）指向外部目录时，
///   `Path.GetFullPath` 不会展开链接，因此**基于软链的逃逸不在本方法的防护范围内**。
///   本系统的存储根目录由部署方自己指定（NAS 挂载点 / 共享卷），
///   只要不把根目录本身做成软链即可；要彻底解决需要逐段解析链接，代价与收益不成比例。
/// </summary>
public static class SafePath
{
    /// <summary>
    /// 把 <paramref name="relativePath"/> 解析成 <paramref name="rootPath"/> 下的绝对路径。
    ///
    /// <paramref name="label"/> 用于错误信息（如「照片路径」「产物路径」「附件路径」），
    /// 让运维一眼看出是哪一处存储拒绝了它。
    /// </summary>
    /// <exception cref="ArgumentException">相对路径为空。</exception>
    /// <exception cref="InvalidOperationException">解析结果超出了根目录。</exception>
    public static string ResolveWithin(string rootPath, string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException($"{label}不能为空。", nameof(relativePath));
        }

        // 相对路径被写成绝对路径时（如 "/etc/passwd"），Path.Combine 会**丢弃**根目录，
        // 于是 combined 直接是那个绝对路径 —— 下面的包含性检查会把它拦下。
        var root = Path.GetFullPath(rootPath);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!IsWithin(root, combined))
        {
            // 错误信息里带上原始相对路径：它可能来自数据库，是排查"谁写脏了库"的唯一线索。
            throw new InvalidOperationException($"非法的{label}「{relativePath}」：超出存储根目录。");
        }

        return combined;
    }

    /// <summary>
    /// 判断 <paramref name="fullPath"/> 是否落在 <paramref name="rootPath"/> 之内
    /// （等于根目录本身也算在内）。两个参数都应是已规范化的绝对路径。
    /// </summary>
    public static bool IsWithin(string rootPath, string fullPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // 去掉末尾多余的分隔符（`/data/photos/` 与 `/data/photos` 等价），
        // 但要保留根目录 `/` 这种情况。
        var root = Path.TrimEndingDirectorySeparator(rootPath);

        if (string.Equals(fullPath, root, comparison))
        {
            return true;
        }

        // ★ 关键：补上结尾分隔符再比前缀。
        //   少了这一步，`/data/photos-evil` 会被当成 `/data/photos` 的子孙。
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, comparison);
    }
}
