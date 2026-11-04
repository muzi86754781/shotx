using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ShotX.Windows.Core;

public static class OcrHelper
{
    private static Windows.Media.Ocr.OcrEngine? _engine;

    public static async Task<string> RecognizeTextAsync(Bitmap bitmap)
    {
        try
        {
            if (_engine == null)
            {
                _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine == null && Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count > 0)
                {
                    _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages[0]);
                }
            }

            if (_engine == null)
            {
                return "未检测到系统 OCR 语言包，请在 Windows 设置中安装中文或英文语言包。";
            }

            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Bmp);
            memory.Position = 0;

            var randomAccessStream = memory.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var ocrResult = await _engine.RecognizeAsync(softwareBitmap);
            return ocrResult.Text;
        }
        catch (Exception ex)
        {
            return $"OCR 识别失败: {ex.Message}";
        }
    }
}
