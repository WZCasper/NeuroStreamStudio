using System;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Накладывает кадр камеры (picture-in-picture) поверх кадра экрана и
    /// отдаёт результат как BGRA-байты, готовые для FFmpegEncoder.WriteVideoFrame.
    ///
    /// Производительность: при 30-60 кадрах в секунду аллокация новых Mat/
    /// массивов на каждый кадр создаёт недопустимое давление на сборщик
    /// мусора (GC) и может приводить к подёргиванию видео (кадры с
    /// задержкой из-за паузы на сборку мусора). Поэтому:
    /// - входные byte[] оборачиваются в Mat БЕЗ копирования данных через
    ///   закреплённый (pinned) GCHandle и конструктор Mat(rows, cols, type,
    ///   IntPtr data, step) - ровно то же место в памяти, никакого дубля;
    /// - масштабированный кадр камеры (_resizedCamera) переиспользуется
    ///   между вызовами Composite(), а не создаётся заново каждый раз,
    ///   если его размер не изменился.
    /// </summary>
    public class CompositorEngine : IDisposable
    {
        private Mat? _resizedCamera;
        private Size _lastCameraTargetSize;

        /// <summary>
        /// Накладывает камеру на экран в заданной позиции (в процентах
        /// холста, как хранится в StreamSettings) и пишет результат в
        /// outputBuffer. screenBuffer изменяется прямо на месте (экран -
        /// это "фон", в него встраивается уменьшенная камера) - отдельный
        /// выходной буфер не аллоцируется, что экономит одну полную копию
        /// кадра экрана на каждый вызов.
        /// </summary>
        public void Composite(
            byte[] screenBuffer, int screenWidth, int screenHeight,
            byte[] cameraBuffer, int cameraWidth, int cameraHeight,
            double cameraLeftPercent, double cameraTopPercent, double cameraWidthPercent)
        {
            if (screenBuffer.Length != screenWidth * screenHeight * 4)
            {
                throw new ArgumentException("Размер screenBuffer не совпадает с screenWidth*screenHeight*4.", nameof(screenBuffer));
            }
            if (cameraBuffer.Length != cameraWidth * cameraHeight * 4)
            {
                throw new ArgumentException("Размер cameraBuffer не совпадает с cameraWidth*cameraHeight*4.", nameof(cameraBuffer));
            }

            // Целевой размер окна камеры в пикселях экрана, по проценту
            // ширины из настроек; высота сохраняет исходное соотношение
            // сторон камеры, чтобы изображение не искажалось.
            int targetWidth = Math.Max(1, (int)(screenWidth * cameraWidthPercent / 100.0));
            double aspectRatio = (double)cameraHeight / cameraWidth;
            int targetHeight = Math.Max(1, (int)(targetWidth * aspectRatio));

            int targetLeft = (int)(screenWidth * cameraLeftPercent / 100.0);
            int targetTop = (int)(screenHeight * cameraTopPercent / 100.0);

            // Окно камеры не должно выходить за пределы холста экрана -
            // обрезаем целевую область по границам, а не падаем с ошибкой,
            // если пользователь выставил позицию/размер, которые вместе
            // вылезают за край (например, 90% left + 25% width).
            targetLeft = Math.Clamp(targetLeft, 0, Math.Max(0, screenWidth - 1));
            targetTop = Math.Clamp(targetTop, 0, Math.Max(0, screenHeight - 1));
            targetWidth = Math.Min(targetWidth, screenWidth - targetLeft);
            targetHeight = Math.Min(targetHeight, screenHeight - targetTop);

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                return; // окно камеры целиком вне холста - наложить нечего
            }

            GCHandle screenHandle = GCHandle.Alloc(screenBuffer, GCHandleType.Pinned);
            GCHandle cameraHandle = GCHandle.Alloc(cameraBuffer, GCHandleType.Pinned);
            try
            {
                using var screenMat = new Mat(
                    screenHeight, screenWidth, MatType.CV_8UC4,
                    screenHandle.AddrOfPinnedObject(), screenWidth * 4);
                using var cameraMat = new Mat(
                    cameraHeight, cameraWidth, MatType.CV_8UC4,
                    cameraHandle.AddrOfPinnedObject(), cameraWidth * 4);

                var targetSize = new Size(targetWidth, targetHeight);
                if (_resizedCamera == null || _lastCameraTargetSize != targetSize)
                {
                    _resizedCamera?.Dispose();
                    _resizedCamera = new Mat(targetHeight, targetWidth, MatType.CV_8UC4);
                    _lastCameraTargetSize = targetSize;
                }

                Cv2.Resize(cameraMat, _resizedCamera, targetSize, interpolation: InterpolationFlags.Linear);

                // ROI (Region Of Interest) - заголовок на подобласть
                // screenMat без копирования; CopyTo в него физически
                // записывает пиксели камеры в нужное место экранного буфера.
                using Mat roi = new(screenMat, new Rect(targetLeft, targetTop, targetWidth, targetHeight));
                _resizedCamera.CopyTo(roi);
            }
            finally
            {
                screenHandle.Free();
                cameraHandle.Free();
            }
        }

        public void Dispose()
        {
            _resizedCamera?.Dispose();
            _resizedCamera = null;
        }
    }
}
