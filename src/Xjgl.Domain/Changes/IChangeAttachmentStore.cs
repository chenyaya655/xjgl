namespace Xjgl.Changes;

/// <summary>
/// 异动附件的存储。
///
/// 与照片存储（<c>IPhotoStorage</c>）**刻意分开**，而不是复用它的
/// 「NAS 主存储 + 本地降级 + 熔断 + 后台补偿回放」那一整套：
///
///   - 照片的补偿回放任务（<c>PhotoCompensationService</c>）只扫描**照片表**里
///     状态为 <c>FallbackPending</c> 的行。附件如果走同一条链路，NAS 故障期间
///     写入的附件会落在降级区，而补偿任务**永远不会回放它**——用户以为传上去了、
///     库里也确实有一条记录，文件却只存在于当时那台机器的本地盘上。
///     这是一条**静默的数据丢失路径**，比"上传直接失败"坏得多。
///   - 因此附件走一条**没有降级、失败就报错**的路径：写不进去就让用户看见
///     "上传失败"，而不是给他一个假的成功。
///
/// 【如实记录它的代价 —— 附件在**服务器本地磁盘**上】
/// 与导出产物同一条约束（见 `deploy/README.md` §11.2）：相对路径存在库里，
/// 文件存在**写它的那个副本**的本地盘上。多副本部署必须给这个目录挂
/// **共享卷**（ReadWriteMany），否则"在 A 副本上传、下载请求落到 B 副本"就是 404。
/// 抽成接口正是为了这件事留口子：将来换对象存储只需再写一个实现。
///
/// 抽成接口的第二个理由是路径规则只能有一份：上传（应用服务）与下载（控制器）
/// 各自拼一次路径，迟早出现"写进去的路径读不出来"。
/// </summary>
public interface IChangeAttachmentStore
{
    /// <summary>存储根目录（绝对路径），仅用于日志与诊断。</summary>
    string RootPath { get; }

    /// <summary>
    /// 写入附件，返回相对路径。
    ///
    /// 物理文件名由**服务端**的 <paramref name="applyId"/> /
    /// <paramref name="attachmentId"/> 与 <paramref name="fileName"/> 的扩展名拼成，
    /// 不用原始文件名做文件名（生僻字、重名、路径穿越都由此一并避开）。
    /// </summary>
    Task<string> SaveAsync(
        Guid applyId,
        Guid attachmentId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>打开只读流；文件不存在返回 <c>null</c>（由调用方决定报什么错）。</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>删除附件；文件不存在视为成功（清理与重复删除都要求幂等）。</summary>
    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}
