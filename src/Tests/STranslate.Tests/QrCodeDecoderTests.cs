using STranslate.Core;
using System.DrawingCore.Imaging;
using ZXing;
using ZXing.QrCode;
using ZXing.ZKWeb;

namespace STranslate.Tests;

public class QrCodeDecoderTests
{
    [Fact]
    public void AutoRecognition_IsEnabledByDefault()
    {
        Assert.True(new Settings().AutoRecognizeQrCodeInOcr);
    }

    [Fact]
    public void Decode_ReturnsUtf8Content_ForQrCodeImage()
    {
        const string content = "https://stranslate.zggsong.com/测试";
        var writer = new BarcodeWriter
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 320,
                Height = 320,
                Margin = 2,
                CharacterSet = "UTF-8"
            }
        };

        using var bitmap = writer.Write(content);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);

        var result = QrCodeDecoder.Decode(stream.ToArray());

        Assert.Null(result.Error);
        Assert.True(result.HasText);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void Decode_ReturnsError_ForInvalidImageData()
    {
        var result = QrCodeDecoder.Decode([0x01, 0x02, 0x03]);

        Assert.False(result.HasText);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("https://example.com/path", true)]
    [InlineData(" HTTP://example.com/path ", true)]
    [InlineData("file:///C:/Windows/System32", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("http:relative-path", false)]
    [InlineData("plain QR code content", false)]
    [InlineData("", false)]
    public void TryGetWebUri_AllowsOnlyHttpAndHttps(string value, bool expected)
    {
        var result = QrCodeDecoder.TryGetWebUri(value, out var uri);

        Assert.Equal(expected, result);
        Assert.Equal(expected, uri is not null);
    }
}
