using System;
using System.IO;

namespace EmbyIcons.ImageProcessing
{
    public interface IImageProcessor : IDisposable
    {
        string Name { get; }
        bool IsAvailable { get; }
        object DecodeImage(Stream inputStream);
        void GetImageDimensions(object image, out int width, out int height);
        object CreateBlankImage(int width, int height);
        void DrawImage(object targetImage, object sourceImage, int x, int y, int width, int height, bool enableSmoothing);
        void DrawText(object image, string text, int x, int y, float fontSize, string color, string fontFamily, bool enableSmoothing);
        void EncodeImage(object image, Stream outputStream, string format, int quality);
        void DisposeImage(object image);
    }
}
