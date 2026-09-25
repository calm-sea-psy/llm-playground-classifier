using SkiaSharp;

namespace Api.Shared.Imaging;

/// <summary>VLM 입력용 이미지 준비 (Text 폴백·Image 소견 공용): EXIF 회전 적용 + 긴 변 축소 + JPEG (2480×3508 원본을 그대로 보내면 토큰·시간이 크게 늘어남)</summary>
public static class ImageResizer
{
    public static byte[] ToJpeg(Stream source, int maxSide, int quality = 90)
    {
        using var codec = SKCodec.Create(source) ?? throw new InvalidOperationException("이미지를 읽을 수 없습니다");
        using var decoded = SKBitmap.Decode(codec);
        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);

        var scale = Math.Min(1f, (float)maxSide / Math.Max(oriented.Width, oriented.Height));
        var width = Math.Max(1, (int)(oriented.Width * scale));
        var height = Math.Max(1, (int)(oriented.Height * scale));
        using var resized = scale < 1f
            ? oriented.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : oriented.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>디코딩 없이 헤더만 읽어 화소 수 (못 읽으면 0)</summary>
    public static long PixelCount(Stream source)
    {
        using var codec = SKCodec.Create(source);
        return codec is null ? 0 : (long)codec.Info.Width * codec.Info.Height;
    }

    private static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var (degrees, swap) = origin switch
        {
            SKEncodedOrigin.BottomRight => (180, false),
            SKEncodedOrigin.RightTop => (90, true),
            SKEncodedOrigin.LeftBottom => (270, true),
            _ => (0, false),
        };
        if (degrees == 0)
        {
            return bitmap.Copy();
        }
        var rotated = new SKBitmap(swap ? bitmap.Height : bitmap.Width, swap ? bitmap.Width : bitmap.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return rotated;
    }
}
