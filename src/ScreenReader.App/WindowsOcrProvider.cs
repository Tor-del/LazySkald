using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScreenReader.App;

public interface IOcrProvider
{
    IReadOnlyList<(string Tag,string Name)> Languages { get; }
    Task<string> RecognizeAsync(Bitmap bitmap, string language, CancellationToken cancellationToken);
}
public interface ITextTransform
{
    Task<string> TransformAsync(string text, string sourceLanguage, CancellationToken cancellationToken);
}
public sealed class IdentityTextTransform : ITextTransform
{
    public Task<string> TransformAsync(string text,string sourceLanguage,CancellationToken cancellationToken) => Task.FromResult(text);
}
public sealed class WindowsOcrProvider : IOcrProvider
{
    public IReadOnlyList<(string Tag,string Name)> Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => (CultureInfo.CreateSpecificCulture(l.LanguageTag).Name,l.DisplayName)).Distinct().ToArray();
    public async Task<string> RecognizeAsync(Bitmap bitmap,string language,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Each queued capture can recognize independently; engines are not shared
        // between overlapping RecognizeAsync calls.
        // Windows can expose neutral OCR tags (e.g. "ru"), while TTS uses "ru-RU".
        var installed = OcrEngine.AvailableRecognizerLanguages;
        var selected = installed.FirstOrDefault(l => l.LanguageTag.Equals(language,StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(l => CultureInfo.CreateSpecificCulture(l.LanguageTag).Name.Equals(language,StringComparison.OrdinalIgnoreCase));
        var engine = selected is null ? null : OcrEngine.TryCreateFromLanguage(selected);
        engine = engine ?? throw new InvalidOperationException(UiText.T("error.ocrMissing",language));
        using var stream = new MemoryStream();
        bitmap.Save(stream,ImageFormat.Png); stream.Position = 0;
        using var random = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
        double scale = Math.Min(2.0, OcrEngine.MaxImageDimension/(double)Math.Max(bitmap.Width,bitmap.Height));
        var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1,Math.Round(bitmap.Width*scale)), ScaledHeight = (uint)Math.Max(1,Math.Round(bitmap.Height*scale)), InterpolationMode = BitmapInterpolationMode.Cubic };
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Ignore,transform,ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(software).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return string.Join(Environment.NewLine,result.Lines.Select(line => line.Text)).Trim();
    }
}
