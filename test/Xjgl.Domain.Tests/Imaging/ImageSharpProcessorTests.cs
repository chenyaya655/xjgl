using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Shouldly;
using Xjgl.Imaging;
using Xunit;

namespace Xjgl.Imaging;

/// <summary>
/// 图像处理（缩略图）的单元测试。
///
/// 【为什么要测这些看似"库已经保证"的行为】
/// 缩略图最容易出的两类问题都不是崩溃，而是**默默画错**：
///   1) 长宽比被压扁/拉伸（人脸变形）或小图被放大（更模糊）；
///   2) 手机照片带 EXIF 方向标记时，缩略图歪 90°。
/// 这些在接口层面都表现为"成功返回了一张图"，只有真的解码出来量尺寸才能发现。
/// </summary>
public class ImageSharpProcessorTests
{
    private readonly ImageSharpProcessor _processor = new();

    private static byte[] Jpeg(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);

        // 画一条对角线，避免纯色图被编码器压成极小体积而看不出差异
        image.Mutate(x => x.BackgroundColor(Color.SkyBlue));

        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);

        return buffer.ToArray();
    }

    private static PhotoImageSize SizeOf(byte[] content)
    {
        using var image = Image.Load(content);

        return new PhotoImageSize(image.Width, image.Height);
    }

    // ==================================================================
    // 尺寸探测
    // ==================================================================

    [Fact]
    public async Task 探测尺寸_能读出真实宽高()
    {
        var size = await _processor.ProbeAsync(Jpeg(800, 600));

        size.ShouldNotBeNull();
        size!.Width.ShouldBe(800);
        size.Height.ShouldBe(600);
    }

    [Fact]
    public async Task 探测尺寸_不是图片时返回null而不是抛异常()
    {
        var notAnImage = System.Text.Encoding.UTF8.GetBytes("这是一个改了后缀的文本文件，不是图片。");

        var size = await _processor.ProbeAsync(notAnImage);

        size.ShouldBeNull("上传接口要能对「改了后缀的 txt」给出明确报错，而不是让整批 500");
    }

    [Fact]
    public async Task 探测尺寸_空内容返回null()
    {
        var size = await _processor.ProbeAsync(ReadOnlyMemory<byte>.Empty);

        size.ShouldBeNull();
    }

    // ==================================================================
    // 缩略图
    // ==================================================================

    [Fact]
    public async Task 缩略图_横图长边压到上限且保持比例()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(Jpeg(1600, 1200), maxEdge: 480);

        thumbnail.ShouldNotBeNull();

        var size = SizeOf(thumbnail!);

        size.Width.ShouldBe(480);
        size.Height.ShouldBe(360, "必须等比缩放：压扁的人脸照是没法用的");
    }

    [Fact]
    public async Task 缩略图_竖图同样按长边压缩()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(Jpeg(1200, 1600), maxEdge: 480);

        var size = SizeOf(thumbnail!);

        size.Height.ShouldBe(480);
        size.Width.ShouldBe(360);
    }

    [Fact]
    public async Task 缩略图_小图不放大()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(Jpeg(200, 150), maxEdge: 480);

        var size = SizeOf(thumbnail!);

        size.Width.ShouldBe(200, "本来就比上限小，放大只会更模糊、体积更大");
        size.Height.ShouldBe(150);
    }

    [Fact]
    public async Task 缩略图_输出是JPEG()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(Jpeg(1000, 800));

        thumbnail.ShouldNotBeNull();
        thumbnail!.Length.ShouldBeGreaterThan(100);

        // JPEG 魔数 FF D8 FF
        thumbnail[0].ShouldBe((byte)0xFF);
        thumbnail[1].ShouldBe((byte)0xD8);
        thumbnail[2].ShouldBe((byte)0xFF);
    }

    [Fact]
    public async Task 缩略图_明显小于原图()
    {
        var original = Jpeg(2400, 1800);
        var thumbnail = await _processor.CreateThumbnailAsync(original);

        // 证件照压到 480 长边后，体积通常是原图的百分之几
        thumbnail!.Length.ShouldBeLessThan(original.Length / 4);
    }

    [Fact]
    public async Task 缩略图_非图片返回null不抛异常()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(
            System.Text.Encoding.UTF8.GetBytes("not an image"));

        thumbnail.ShouldBeNull("缩略图是锦上添花，生成不出来不该让照片上传失败");
    }

    [Fact]
    public async Task 缩略图_上限为零时返回null()
    {
        var thumbnail = await _processor.CreateThumbnailAsync(Jpeg(100, 100), maxEdge: 0);

        thumbnail.ShouldBeNull();
    }
}
