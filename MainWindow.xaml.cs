using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NeuroStreamStudio
{
    public partial class MainWindow : Window
    {
        private readonly StreamSettings _settings;

        private readonly ScreenCaptureEngine _screenEngine = new();
        private readonly CameraCaptureEngine _cameraEngine = new();
        private readonly CompositorEngine _compositor = new();
        private readonly FFmpegEncoder _encoder = new();

        private IReadOnlyList<ScreenDescriptor> _screens = Array.Empty<ScreenDescriptor>();
        private IReadOnlyList<CameraDescriptor> _cameras = Array.Empty<CameraDescriptor>();

        private byte[]? _screenFrameBuffer;
        private byte[]? _cameraFrameBuffer;
        private byte[]? _previewFrameBuffer;

        private CancellationTokenSource? _captureLoopCts;
        private Task? _captureLoopTask;
        private bool _isStreaming;

        public MainWindow()
        {
            InitializeComponent();

            _settings = StreamSettings.Load();
            _encoder.LogLine += OnEncoderLogLine;
            _encoder.ProcessExited += OnEncoderProcessExited;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckFFmpegAvailableAsync();
            PopulateScreens();
            PopulateCameras();
            await PopulateAudioDevicesAsync();
            RestoreSettingsIntoUi();
        }

        // ==================== Инициализация и проверки ====================

        private async Task CheckFFmpegAvailableAsync()
        {
            string? path = FFmpegLocator.Find();
            if (path == null)
            {
                StatusText.Text = "ffmpeg.exe не найден — переустановите NeuroStreamStudio из последнего релиза";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StartButton.IsEnabled = false;
                return;
            }

            bool works = await Task.Run(() => FFmpegLocator.VerifyWorks(path));
            if (!works)
            {
                StatusText.Text = "ffmpeg.exe найден, но не запускается — файл может быть повреждён";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                StartButton.IsEnabled = false;
            }
        }

        private void PopulateScreens()
        {
            _screens = ScreenCaptureEngine.ListScreens();
            ScreenComboBox.ItemsSource = _screens.Select(s => s.Label).ToList();
            if (_screens.Count > 0)
            {
                int indexToSelect = Math.Min(_settings.SelectedScreenIndex, _screens.Count - 1);
                ScreenComboBox.SelectedIndex = indexToSelect;
            }
        }

        private void PopulateCameras()
        {
            _cameras = CameraCaptureEngine.ListCameras();
            CameraComboBox.ItemsSource = _cameras.Select(c => c.Label).ToList();
            if (_cameras.Count > 0)
            {
                CameraComboBox.SelectedIndex = 0;
            }
        }

        private async Task PopulateAudioDevicesAsync()
        {
            AudioDeviceList devices = await AudioDeviceEnumerator.ListAsync();

            if (devices.Error != null)
            {
                AppendLog($"Не удалось получить список аудио-устройств: {devices.Error}");
                return;
            }

            MicrophoneComboBox.ItemsSource = devices.DeviceNames;
            SystemAudioComboBox.ItemsSource = devices.DeviceNames;

            // Пустой выбор ("без микрофона"/"без системного звука") тоже
            // должен быть доступен - добавляем явный пункт в начало списка,
            // а не заставляем пользователя всегда выбирать какое-то устройство.
            var micList = new List<string> { "(не использовать)" };
            micList.AddRange(devices.DeviceNames);
            MicrophoneComboBox.ItemsSource = micList;

            var sysList = new List<string> { "(не использовать)" };
            sysList.AddRange(devices.DeviceNames);
            SystemAudioComboBox.ItemsSource = sysList;

            MicrophoneComboBox.SelectedIndex = 0;

            // Предлагаем разумный выбор по умолчанию для системного звука,
            // если среди устройств нашлось что-то похожее на loopback -
            // пользователю не нужно знать заранее, что искать.
            int suggestedIndex = devices.DeviceNames
                .Select((name, idx) => (name, idx))
                .Where(x => AudioDeviceEnumerator.LooksLikeSystemAudioDevice(x.name))
                .Select(x => x.idx + 1) // +1 из-за "(не использовать)" в начале списка
                .FirstOrDefault(-1);
            SystemAudioComboBox.SelectedIndex = suggestedIndex >= 0 ? suggestedIndex : 0;
        }

        private void RestoreSettingsIntoUi()
        {
            RtmpUrlTextBox.Text = _settings.RtmpUrl;
            StreamKeyPasswordBox.Password = _settings.StreamKeyPlainText;
            BitrateComboBox.SelectedIndex = _settings.VideoBitrateKbps switch
            {
                <= 3000 => 0,
                <= 6000 => 1,
                _ => 2,
            };
        }

        // ==================== Старт/стоп трансляции ====================

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            string rtmpUrl = RtmpUrlTextBox.Text.Trim();
            string streamKey = StreamKeyPasswordBox.Password.Trim();

            if (string.IsNullOrEmpty(rtmpUrl) || string.IsNullOrEmpty(streamKey))
            {
                MessageBox.Show(
                    "Заполните Server URL и Stream Key — их можно получить в панели трансляции вашей площадки " +
                    "(например, livecenter.tiktok.com/producer для TikTok LIVE).",
                    "NeuroStreamStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ScreenComboBox.SelectedIndex < 0)
            {
                MessageBox.Show("Выберите экран для захвата.", "NeuroStreamStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveUiIntoSettings(rtmpUrl, streamKey);

            try
            {
                StartButton.IsEnabled = false;
                StatusText.Text = "Запуск...";

                ScreenDescriptor screen = _screens[ScreenComboBox.SelectedIndex];
                _screenEngine.Start(screen);

                if (_cameras.Count > 0 && CameraComboBox.SelectedIndex >= 0)
                {
                    CameraDescriptor camera = _cameras[CameraComboBox.SelectedIndex];
                    _cameraEngine.Start(camera.DeviceIndex);
                }

                _screenFrameBuffer = new byte[_screenEngine.Width * _screenEngine.Height * 4];
                _previewFrameBuffer = new byte[_screenFrameBuffer.Length];
                if (_cameraEngine.Width > 0)
                {
                    _cameraFrameBuffer = new byte[_cameraEngine.Width * _cameraEngine.Height * 4];
                }

                string? micDevice = GetSelectedDeviceOrNull(MicrophoneComboBox);
                string? sysDevice = GetSelectedDeviceOrNull(SystemAudioComboBox);

                _encoder.Start(new FFmpegStartOptions
                {
                    RtmpUrl = rtmpUrl,
                    StreamKey = streamKey,
                    Width = _screenEngine.Width,
                    Height = _screenEngine.Height,
                    Fps = 30,
                    VideoBitrateKbps = _settings.VideoBitrateKbps,
                    AudioBitrateKbps = _settings.AudioBitrateKbps,
                    MicrophoneDeviceName = micDevice,
                    SystemAudioDeviceName = sysDevice,
                });

                _captureLoopCts = new CancellationTokenSource();
                _captureLoopTask = Task.Run(() => CaptureLoop(_captureLoopCts.Token));

                _isStreaming = true;
                StartButton.Visibility = Visibility.Collapsed;
                StopButton.Visibility = Visibility.Visible;
                StatusText.Text = "🔴 В эфире";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(63, 185, 80));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось начать трансляцию:\n\n{ex.Message}", "NeuroStreamStudio",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StopEverything();
            }
            finally
            {
                StartButton.IsEnabled = true;
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopEverything();
        }

        private static string? GetSelectedDeviceOrNull(System.Windows.Controls.ComboBox comboBox)
        {
            if (comboBox.SelectedIndex <= 0) // индекс 0 - это "(не использовать)"
            {
                return null;
            }
            return comboBox.SelectedItem as string;
        }

        private void SaveUiIntoSettings(string rtmpUrl, string streamKey)
        {
            _settings.RtmpUrl = rtmpUrl;
            _settings.StreamKeyPlainText = streamKey;
            _settings.VideoBitrateKbps = BitrateComboBox.SelectedIndex switch
            {
                0 => 3000,
                2 => 9000,
                _ => 6000,
            };
            _settings.SelectedScreenIndex = ScreenComboBox.SelectedIndex;

            (_settings.CameraLeftPercent, _settings.CameraTopPercent) = CameraPositionComboBox.SelectedIndex switch
            {
                0 => (70.0, 65.0),  // снизу справа
                1 => (2.0, 65.0),   // снизу слева
                2 => (70.0, 2.0),   // сверху справа
                _ => (2.0, 2.0),    // сверху слева
            };
            _settings.CameraWidthPercent = CameraSizeComboBox.SelectedIndex switch
            {
                0 => 18.0,
                2 => 35.0,
                _ => 25.0,
            };

            _settings.Save();
        }

        private void StopEverything()
        {
            _isStreaming = false;

            _captureLoopCts?.Cancel();
            try
            {
                _captureLoopTask?.Wait(2000);
            }
            catch
            {
                // Таск мог завершиться с OperationCanceledException - ожидаемо.
            }

            _encoder.Stop();
            _cameraEngine.Stop();
            _screenEngine.Stop();

            Dispatcher.Invoke(() =>
            {
                StartButton.Visibility = Visibility.Visible;
                StopButton.Visibility = Visibility.Collapsed;
                StatusText.Text = "Остановлено";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            });
        }

        // ==================== Цикл захвата и композитинга ====================

        private void CaptureLoop(CancellationToken ct)
        {
            const int targetFps = 30;
            int frameIntervalMs = 1000 / targetFps;
            int previewFrameCounter = 0;

            while (!ct.IsCancellationRequested)
            {
                DateTime frameStart = DateTime.UtcNow;

                bool screenOk = _screenFrameBuffer != null && _screenEngine.TryCaptureFrame(_screenFrameBuffer);
                if (!screenOk || _screenFrameBuffer == null)
                {
                    SleepRemaining(frameStart, frameIntervalMs);
                    continue;
                }

                if (_cameraFrameBuffer != null)
                {
                    bool cameraOk = _cameraEngine.TryCaptureFrame(_cameraFrameBuffer);
                    if (cameraOk)
                    {
                        _compositor.Composite(
                            _screenFrameBuffer, _screenEngine.Width, _screenEngine.Height,
                            _cameraFrameBuffer, _cameraEngine.Width, _cameraEngine.Height,
                            _settings.CameraLeftPercent, _settings.CameraTopPercent, _settings.CameraWidthPercent);
                    }
                }

                _encoder.WriteVideoFrame(_screenFrameBuffer);

                // Превью в UI обновляется реже полного FPS трансляции
                // (каждый 3-й кадр, т.е. ~10 раз в секунду) - обновление
                // WPF Image на каждом кадре 30 раз в секунду создало бы
                // заметную нагрузку на UI-поток без видимой пользователю
                // пользы (глаз не различает разницу в превью).
                previewFrameCounter++;
                if (previewFrameCounter >= 3 && _previewFrameBuffer != null)
                {
                    previewFrameCounter = 0;
                    Array.Copy(_screenFrameBuffer, _previewFrameBuffer, _screenFrameBuffer.Length);
                    int w = _screenEngine.Width;
                    int h = _screenEngine.Height;
                    Dispatcher.BeginInvoke(() => UpdatePreview(_previewFrameBuffer, w, h));
                }

                SleepRemaining(frameStart, frameIntervalMs);
            }
        }

        private static void SleepRemaining(DateTime frameStart, int frameIntervalMs)
        {
            double elapsedMs = (DateTime.UtcNow - frameStart).TotalMilliseconds;
            int remainingMs = frameIntervalMs - (int)elapsedMs;
            if (remainingMs > 0)
            {
                Thread.Sleep(remainingMs);
            }
            // Если кадр обработался дольше интервала - не спим вообще
            // (пропускаем кадр по времени, но не блокируем поток ещё
            // сильнее), что позволяет циклу нагнать график, если на одном
            // кадре возникла временная задержка.
        }

        private void UpdatePreview(byte[] bgraBuffer, int width, int height)
        {
            try
            {
                var bitmap = BitmapSource.Create(
                    width, height, 96, 96, PixelFormats.Bgra32, null,
                    bgraBuffer, width * 4);
                bitmap.Freeze();
                PreviewImage.Source = bitmap;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Превью - не критичная функциональность; сбой здесь не
                // должен прерывать саму трансляцию.
            }
        }

        // ==================== Обработка событий энкодера ====================

        private void OnEncoderLogLine(object? sender, string line)
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
        }

        private void OnEncoderProcessExited(object? sender, int exitCode)
        {
            if (!_isStreaming)
            {
                return; // мы сами его остановили - это не неожиданное завершение
            }

            Dispatcher.BeginInvoke(() =>
            {
                string[] recentLog = _encoder.RecentLog.Snapshot();
                string details = recentLog.Length > 0
                    ? string.Join("\n", recentLog.TakeLast(10))
                    : "(нет дополнительных сведений)";

                MessageBox.Show(
                    $"Трансляция неожиданно завершилась (код {exitCode}).\n\n" +
                    $"Последние сообщения ffmpeg:\n{details}",
                    "NeuroStreamStudio — трансляция прервана",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                StopEverything();
            });
        }

        private void AppendLog(string line)
        {
            LogText.Text = line;
        }

        // ==================== Закрытие приложения ====================

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isStreaming)
            {
                StopEverything();
            }

            _encoder.Dispose();
            _cameraEngine.Dispose();
            _screenEngine.Dispose();
            _compositor.Dispose();
        }
    }
}
