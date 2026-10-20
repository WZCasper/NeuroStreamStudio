using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Захват веб-камеры через OpenCvSharp4 - тот же подход, что уже
    /// используется в NeuroCamera (тот же разработчик, та же технология),
    /// для согласованности между проектами NEURO. NeuroStreamStudio НЕ переиспользует
    /// автокалибровку/цветокоррекцию NeuroCamera (это отдельная, самостоятельная
    /// функциональность того приложения) - здесь нужен только сырой кадр с
    /// камеры для наложения на экран, без цветокоррекции.
    ///
    /// Если пользователь уже откалибровал камеру через сам NeuroCamera и
    /// запустил его, разумный будущий шаг - дать NeuroStreamStudio опционально
    /// забирать уже обработанный кадр у NeuroCamera вместо повторного
    /// захвата сырого видео с устройства. Для первой версии это не входит
    /// в объём работы - камера захватывается независимо и напрямую.
    /// </summary>
    public class CameraCaptureEngine : IDisposable
    {
        private VideoCapture? _capture;
        private Mat? _frame;

        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>
        /// Перечисляет доступные камеры по индексу устройства. OpenCvSharp
        /// (как и OpenCV в целом) не даёт API для получения понятных имён
        /// устройств на Windows без дополнительного DirectShow-интеропа -
        /// поэтому камеры показываются пользователю как "Камера 0",
        /// "Камера 1" и т.д., с возможностью посмотреть превью перед
        /// выбором (см. MainWindow - превью показывается сразу после
        /// выбора индекса, до запуска самого стрима).
        /// </summary>
        public static IReadOnlyList<CameraDescriptor> ListCameras(int maxIndexToProbe = 5)
        {
            var result = new List<CameraDescriptor>();

            for (int i = 0; i < maxIndexToProbe; i++)
            {
                using var probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                if (probe.IsOpened())
                {
                    result.Add(new CameraDescriptor(i, $"Камера {i}"));
                    probe.Release();
                }
                else
                {
                    // Индексы устройств в DirectShow идут подряд без
                    // пропусков - как только очередной индекс не открылся,
                    // дальше проверять не имеет смысла.
                    break;
                }
            }

            return result;
        }

        public void Start(int deviceIndex, int requestedWidth = 1280, int requestedHeight = 720)
        {
            if (_capture != null)
            {
                throw new InvalidOperationException("Захват камеры уже запущен. Остановите перед повторным запуском.");
            }

            _capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                _capture.Dispose();
                _capture = null;
                throw new InvalidOperationException($"Не удалось открыть камеру с индексом {deviceIndex}.");
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, requestedWidth);
            _capture.Set(VideoCaptureProperties.FrameHeight, requestedHeight);

            // Фактически согласованное разрешение может отличаться от
            // запрошенного - камера поддерживает не любое сочетание,
            // поэтому читаем то, что реально установилось, а не то, что
            // запросили.
            Width = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            Height = (int)_capture.Get(VideoCaptureProperties.FrameHeight);

            _frame = new Mat();
        }

        /// <summary>
        /// Захватывает один кадр камеры в виде BGRA-байт (для единообразия
        /// с ScreenCaptureEngine и с тем, что ожидает ffmpeg на входе
        /// композитора). OpenCV по умолчанию отдаёт BGR (3 канала, без
        /// альфы) - конвертация в BGRA (4 канала) нужна явно.
        /// </summary>
        public bool TryCaptureFrame(byte[] destinationBgraBuffer)
        {
            if (_capture == null || _frame == null)
            {
                throw new InvalidOperationException("Захват камеры не запущен. Вызовите Start() перед TryCaptureFrame().");
            }

            if (!_capture.Read(_frame) || _frame.Empty())
            {
                return false;
            }

            using var bgraFrame = new Mat();
            Cv2.CvtColor(_frame, bgraFrame, ColorConversionCodes.BGR2BGRA);

            // Total() и ElemSize() в OpenCvSharp оба возвращают long (Int64);
            // их произведение - полный размер буфера кадра в байтах
            // (Total() = количество пикселей, ElemSize() = байт на пиксель
            // для всех 4 каналов BGRA сразу, т.е. 4 для CV_8UC4).
            long expectedLength = (long)Width * Height * 4;
            long actualLength = bgraFrame.Total() * bgraFrame.ElemSize();
            if (actualLength != expectedLength || destinationBgraBuffer.Length != expectedLength)
            {
                // Кадр камеры внезапно другого размера (устройство сменило
                // режим само, что иногда бывает при автофокусе/автоэкспозиции
                // некоторых вебкамер) - пропускаем кадр, не считаем фатальным.
                return false;
            }

            // Marshal.Copy принимает длину как int, а не long - приведение
            // безопасно здесь: даже 8K-кадр (7680x4320x4 байта) далеко не
            // достигает int.MaxValue, переполнение практически невозможно
            // при реалистичных разрешениях экрана/камеры.
            System.Runtime.InteropServices.Marshal.Copy(
                bgraFrame.Data, destinationBgraBuffer, 0, (int)expectedLength);
            return true;
        }

        public void Stop()
        {
            _frame?.Dispose();
            _frame = null;

            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }

        public void Dispose() => Stop();
    }

    public class CameraDescriptor
    {
        public int DeviceIndex { get; }
        public string Label { get; }

        public CameraDescriptor(int deviceIndex, string label)
        {
            DeviceIndex = deviceIndex;
            Label = label;
        }
    }
}
