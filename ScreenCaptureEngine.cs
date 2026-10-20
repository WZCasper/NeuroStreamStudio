using System;
using System.Collections.Generic;
using System.Linq;
using ScreenCapture.NET;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Захват экрана через ScreenCapture.NET.DX11 (DXGI Desktop Duplication).
    ///
    /// Сознательно НЕ используется "чистый" Windows.Graphics.Capture WinRT API
    /// напрямую: он требует COM-интероп через CsWinRT (HSTRING-маршалинг,
    /// ComWrappers-регистрация), который у WPF-приложений на .NET 9+
    /// конфликтует с собственным COM-интеропом WPF - задокументированная
    /// проблема в экосистеме (см. обсуждения пакета WgcSharp). DXGI Desktop
    /// Duplication, на котором строится ScreenCapture.NET.DX11, - более
    /// старый, но полностью managed-совместимый путь без этого конфликта,
    /// и не завязан на WinRT вообще.
    ///
    /// ИЗВЕСТНОЕ ОГРАНИЧЕНИЕ: DXGI Desktop Duplication может не работать на
    /// гибридных GPU-системах (ноутбук с интегрированной + дискретной
    /// видеокартой, где вывод подключён не к той GPU, что рендерит рабочий
    /// стол) - см. https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/error-when-dda-capable-app-is-against-gpu.
    /// Если захват не запускается, в первую очередь стоит проверить, какая
    /// видеокарта в Windows назначена для рендеринга рабочего стола
    /// (Параметры → Экран → Графика).
    /// </summary>
    public class ScreenCaptureEngine : IDisposable
    {
        private DX11ScreenCaptureService? _service;
        private IScreenCapture? _capture;
        private ICaptureZone? _zone;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public static IReadOnlyList<ScreenDescriptor> ListScreens()
        {
            var service = new DX11ScreenCaptureService();
            var result = new List<ScreenDescriptor>();

            try
            {
                IEnumerable<GraphicsCard> graphicsCards = service.GetGraphicsCards();
                int screenIndex = 0;

                foreach (GraphicsCard card in graphicsCards)
                {
                    foreach (Display display in service.GetDisplays(card))
                    {
                        result.Add(new ScreenDescriptor(
                            screenIndex,
                            display,
                            $"Экран {screenIndex + 1} ({display.Width}x{display.Height})"));
                        screenIndex++;
                    }
                }
            }
            finally
            {
                // DX11ScreenCaptureService не реализует IDisposable в
                // текущей версии пакета - ничего освобождать здесь не нужно,
                // блок finally оставлен пустым намеренно на случай будущих
                // версий пакета, где это может измениться.
            }

            return result;
        }

        /// <summary>
        /// Запускает захват указанного экрана целиком. Для захвата только
        /// окна конкретного приложения (не всего экрана) понадобился бы
        /// другой путь - DX11 Desktop Duplication захватывает весь дисплей,
        /// а не отдельные окна (в отличие от Windows.Graphics.Capture).
        /// Для первой версии это приемлемо: большинство стримеров транслируют
        /// весь экран в полноэкранном режиме игры.
        /// </summary>
        public void Start(ScreenDescriptor screen)
        {
            if (_capture != null)
            {
                throw new InvalidOperationException("Захват экрана уже запущен. Остановите перед повторным запуском.");
            }

            _service = new DX11ScreenCaptureService();
            _capture = _service.GetScreenCapture(screen.Display);
            _zone = _capture.RegisterCaptureZone(0, 0, screen.Display.Width, screen.Display.Height);

            Width = screen.Display.Width;
            Height = screen.Display.Height;
        }

        /// <summary>
        /// Захватывает один кадр и копирует его как BGRA-байты в
        /// предоставленный буфер (переиспользуемый вызывающим кодом, чтобы
        /// не аллоцировать массив на каждый кадр при 30-60 FPS).
        /// Возвращает false, если кадр захватить не удалось (например,
        /// дисплей временно недоступен при смене разрешения) - в этом
        /// случае вызывающий код должен просто повторить попытку на
        /// следующем тике, не считать это фатальной ошибкой.
        /// </summary>
        public bool TryCaptureFrame(byte[] destinationBuffer)
        {
            if (_capture == null || _zone == null)
            {
                throw new InvalidOperationException("Захват экрана не запущен. Вызовите Start() перед TryCaptureFrame().");
            }

            try
            {
                _capture.CaptureScreen();
            }
            catch
            {
                return false;
            }

            using IDisposable _ = _zone.Lock();
            ReadOnlySpan<byte> raw = _zone.RawBuffer;

            if (raw.Length != destinationBuffer.Length)
            {
                // Размер кадра изменился (например, пользователь сменил
                // разрешение экрана на лету) - вызывающий код должен
                // пересоздать буфер под новый размер и попробовать снова.
                return false;
            }

            raw.CopyTo(destinationBuffer);
            return true;
        }

        public void Stop()
        {
            _zone = null;
            _capture = null;
            _service = null;
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Описывает один доступный для захвата экран - обёртка над
    /// ScreenCapture.NET.Display с удобным для UI индексом и подписью.
    /// </summary>
    public class ScreenDescriptor
    {
        public int Index { get; }
        public Display Display { get; }
        public string Label { get; }

        public ScreenDescriptor(int index, Display display, string label)
        {
            Index = index;
            Display = display;
            Label = label;
        }
    }
}
