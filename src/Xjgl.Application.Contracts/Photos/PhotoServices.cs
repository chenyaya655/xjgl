using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Xjgl.Photos.Dtos;

namespace Xjgl.Photos;

/// <summary>
/// 照片相关的容量上限。
///
/// 放在契约层而不是实现层：HTTP 入口与上传实现都要用同一个上限，
/// 而它们分属不同的程序集（HttpApi 不引用 Application），
/// 把数字写两遍早晚会漂移。
/// </summary>
public static class PhotoLimits
{
    /// <summary>单批最大张数。超过请前端分批。</summary>
    public const int MaxBatchSize = 100;

    /// <summary>同时在途的存储写入数。NAS 是共享设备，压太狠反而整体变慢。</summary>
    public const int StorageConcurrency = 4;
}

/// <summary>
/// 照片管理的对外应用服务（查询 / 删除 / 缺照片名单 / 存储健康度 / 补偿）。
///
/// 注意：**上传不走这里**。上传需要 <see cref="Stream"/> 入参（multipart 流式接收），
/// 而 ABP 的自动 API 控制器是基于 DTO 的，把 Stream 塞进 DTO 会强制调用方
/// 把整张照片 base64 编码（体积 +33%）再放进 JSON。
/// 因此上传由 <see cref="IPhotoUploadService"/> 提供，
/// HTTP 层用一个手写控制器接收 multipart 后再调用它。
/// </summary>
public interface IPhotoAppService : IApplicationService
{
    Task<PagedResultDto<PhotoDto>> GetListAsync(PhotoQueryDto input);

    Task<PhotoDto> GetAsync(Guid id);

    /// <summary>删除单张照片（同时清理 NAS 与本地降级区里的文件）。</summary>
    Task DeleteAsync(Guid id);

    /// <summary>批量删除，返回实际删除的条数。</summary>
    Task<int> DeleteManyAsync(List<Guid> ids);

    /// <summary>缺照片（或照片异常）的学生名单 —— 照片管理的核心台账。</summary>
    Task<PagedResultDto<MissingPhotoStudentDto>> GetMissingPhotoListAsync(MissingPhotoQueryDto input);

    /// <summary>照片存储健康度：NAS 是否可写、熔断器状态、待补偿与失败条数。</summary>
    Task<PhotoStorageHealthDto> GetStorageHealthAsync();

    /// <summary>手动触发一次降级照片回放（运维入口，后台任务会自动做同样的事）。</summary>
    Task<PhotoCompensationResultDto> CompensateAsync();
}

/// <summary>
/// 照片上传播接口（流式）。
///
/// 为什么不继承 <c>IApplicationService</c>：ABP 会为应用服务自动生成 API 控制器，
/// 而本接口的参数含 <see cref="Stream"/>，自动生成会失败或不安全。
/// HTTP 入口是 <c>PhotosController</c>（手写，接收 multipart/form-data）。
/// </summary>
public interface IPhotoUploadService
{
    /// <summary>
    /// 批量上传。单个学生失败不影响其他人 —— 教师一次传 50 张时，
    /// 返回结果里会逐条说明哪几个成功、哪几个走了降级、哪几个失败。
    /// </summary>
    /// <param name="batchId">批次 Id，由调用方生成，同一个批次的前端进度条靠它聚合。</param>
    Task<PhotoUploadResultDto> UploadBatchAsync(
        Guid batchId,
        IReadOnlyList<PhotoUploadRequest> requests,
        CancellationToken cancellationToken = default);

    /// <summary>上传单张（学生照片换新、小程序拍照上传等场景）。</summary>
    Task<PhotoUploadItemResultDto> UploadOneAsync(
        PhotoUploadRequest request,
        Guid? batchId = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 降级照片的补偿回放。
///
/// 单独抽出来而不是塞进应用服务，是因为它有两个调用方：
///   1) 运维/管理员手工点「立即补偿」（经 <see cref="IPhotoAppService"/> 鉴权后转调）；
///   2) Host 里的后台任务，NAS 恢复后自动回放。
/// 后台任务没有当前用户，走不了鉴权，因此不能直接复用应用服务。
/// </summary>
public interface IPhotoCompensationService
{
    /// <summary>
    /// 把「降级待补偿」的照片回放到主存储。
    /// 主存储仍不可用时立即中止（不空转），返回 <c>Aborted = true</c>。
    /// </summary>
    /// <param name="maxCount">单轮最多处理多少条，避免一次性打满 NAS。</param>
    Task<PhotoCompensationResultDto> CompensateAsync(
        int maxCount = 500,
        CancellationToken cancellationToken = default);
}

/// <summary>读取到的照片内容。</summary>
public sealed class PhotoContentResult
{
    public byte[] Content { get; init; } = Array.Empty<byte>();

    public string ContentType { get; init; } = "image/jpeg";

    /// <summary>内容哈希（控制器用它做 ETag，客户端可据此走 304）。</summary>
    public string? ETag { get; init; }

    /// <summary>实际返回的是缩略图还是原图（缩略图缺失时会回落成原图）。</summary>
    public bool IsThumbnail { get; init; }
}

/// <summary>
/// 读取学生照片内容（原图 / 缩略图）。
///
/// 与打印服务同样是"产出二进制"的能力，因此**不实现 IApplicationService**：
/// 由 <c>PhotosController</c> 显式设置响应头与缓存，而不是被序列化成 base64。
///
/// 数据范围按**学生**判定（照片是学生档案的一部分）：
/// 学生看不到，他的照片（含缩略图）也不给。
/// </summary>
public interface IPhotoContentService
{
    /// <summary>读取照片；没有照片时返回 null（控制器据此回 404）。</summary>
    Task<PhotoContentResult?> GetAsync(
        Guid studentId,
        bool thumbnail,
        CancellationToken cancellationToken = default);
}
