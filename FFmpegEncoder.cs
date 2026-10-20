using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Запускает ffmpeg как подпроцесс и кодирует живой стрим в RTMP.
    ///
    /// Видео подаётся в ffmpeg через stdin (pipe:0) как сырые кадры BGRA -
    /// их пишет CompositorEngine (экран + наложенная камера) кадр за кадром.
    /// Аудио подаётся НЕ через тот же stdin (один канал не годится для двух
    /// независимых потоков без самодельного мультиплексирования), а как
    /// два отдельных dshow-входа самого ffmpeg: микрофон и системный звук
    /// (loopback-устройство), смикшированные фильтром amix. Это надёжнее и
    /// проще, чем городить синхронизацию двух потоков в коде приложения -
    /// ffmpeg уже умеет синхронизировать несколько входов сам.
    ///
    /// ВАЖНО: системный звук требует loopback-устройства (Stereo Mix или
    /// VB-Audio Virtual Cable) - Windows НЕ даёт его по умолчанию. Если
    /// пользователь не выбрал такое устройство, стрим идёт без звука игры
    /// (только микрофон) - см. AudioDeviceEnumerator.
    /// </summary>
    public class FFmpegEncoder : IDisposable
    {
        private Process? _process;
        private Stream? _videoStdin;
        private readonly object _writeLock = new();
        private CancellationTokenSource? _stderrCts;
        private Task? _stderrReaderTask;

        public event EventHandler<string>? LogLine;
        public event EventHandler<int>? ProcessExited;

        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>
        /// Последние строки stderr ffmpeg - полезно показать пользователю
        /// при ошибке запуска (неверный ключ, недоступен RTMP-сервер и т.д.),
        /// т.к. ffmpeg сообщает причину именно в stderr, не в коде возврата.
        /// </summary>
        public RecentLinesBuffer RecentLog { get; } = new(50);

        public void Start(FFmpegStartOptions options)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("FFmpeg уже запущен. Остановите текущий стрим перед запуском нового.");
            }

            string? ffmpegPath = FFmpegLocator.Find();
            if (ffmpegPath == null)
            {
                throw new FileNotFoundException(
                    "Не найден ffmpeg.exe. Переустановите NeuroStreamStudio из последнего релиза " +
                    "(ffmpeg.exe должен лежать рядом с NeuroStreamStudio.exe) - см. README.");
            }

            string args = BuildArguments(options);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) =>
            {
                ProcessExited?.Invoke(this, _process?.ExitCode ?? -1);
            };

            if (!_process.Start())
            {
                throw new InvalidOperationException("Не удалось запустить процесс ffmpeg.");
            }

            _videoStdin = _process.StandardInput.BaseStream;

            _stderrCts = new CancellationTokenSource();
            _stderrReaderTask = ReadStderrLoop(_process, _stderrCts.Token);
        }

        /// <summary>
        /// Пишет один кадр сырых BGRA-байт в stdin ffmpeg. Вызывается из
        /// CompositorEngine на каждом тике рендера (см. StreamSettings.Fps).
        /// Потокобезопасно относительно одновременного вызова Stop().
        /// </summary>
        public void WriteVideoFrame(byte[] bgraFrameBytes)
        {
            lock (_writeLock)
            {
                if (_videoStdin == null || !IsRunning)
                {
                    return; // стрим уже остановлен или ещё не запущен - просто пропускаем кадр
                }

                try
                {
                    _videoStdin.Write(bgraFrameBytes, 0, bgraFrameBytes.Length);
                }
                catch (IOException)
                {
                    // ffmpeg мог закрыть свой конец pipe (упал, оборвалась сеть
                    // до RTMP-сервера) - ProcessExited сообщит об этом вызывающему
                    // коду отдельно, здесь просто не даём записи в закрытый
                    // pipe уронить поток захвата кадров.
                }
                catch (ObjectDisposedException)
                {
                    // Stop() уже закрыл stdin между проверкой IsRunning и Write.
                }
            }
        }

        public void Stop()
        {
            lock (_writeLock)
            {
                try
                {
                    _videoStdin?.Flush();
                    _videoStdin?.Close();
                }
                catch
                {
                    // Поток уже мог быть закрыт или процесс завершился сам.
                }
                _videoStdin = null;
            }

            _stderrCts?.Cancel();

            if (_process != null && !_process.HasExited)
            {
                // Даём ffmpeg секунду на штатное завершение (закрытие stdin -
                // это сигнал EOF, по которому ffmpeg сам корректно завершает
                // RTMP-сессию), и только потом принудительно убиваем.
                if (!_process.WaitForExit(3000))
                {
                    try { _process.Kill(entireProcessTree: true); } catch { /* уже мог завершиться */ }
                }
            }

            _process?.Dispose();
            _process = null;
        }

        private async Task ReadStderrLoop(Process process, CancellationToken ct)
        {
            try
            {
                using var reader = process.StandardError;
                string? line;
                while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
                {
                    RecentLog.Add(line);
                    LogLine?.Invoke(this, line);
                }
            }
            catch (OperationCanceledException)
            {
                // Остановка стрима - ожидаемо, не ошибка.
            }
            catch
            {
                // Процесс завершился и поток stderr оборвался - ProcessExited
                // уже сообщит о завершении отдельно.
            }
        }

        private static string BuildArguments(FFmpegStartOptions options)
        {
            var sb = new StringBuilder();
            sb.Append("-y -loglevel warning ");

            // ---------- Вход: видео (сырые BGRA-кадры через stdin) ----------
            sb.Append($"-f rawvideo -pix_fmt bgra -video_size {options.Width}x{options.Height} ");
            sb.Append($"-framerate {options.Fps} -i pipe:0 ");

            // ---------- Вход(ы): аудио ----------
            bool hasMic = !string.IsNullOrWhiteSpace(options.MicrophoneDeviceName);
            bool hasLoopback = !string.IsNullOrWhiteSpace(options.SystemAudioDeviceName);

            if (hasMic)
            {
                sb.Append($"-f dshow -i audio=\"{EscapeDshowName(options.MicrophoneDeviceName!)}\" ");
            }
            if (hasLoopback)
            {
                sb.Append($"-f dshow -i audio=\"{EscapeDshowName(options.SystemAudioDeviceName!)}\" ");
            }

            // ---------- Кодирование видео ----------
            sb.Append($"-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p ");
            sb.Append($"-b:v {options.VideoBitrateKbps}k -maxrate {options.VideoBitrateKbps}k ");
            sb.Append($"-bufsize {options.VideoBitrateKbps * 2}k -g {options.Fps * 2} ");

            // ---------- Кодирование/смешивание аудио ----------
            if (hasMic && hasLoopback)
            {
                // Входы аудио - это потоки 1 и 2 (0 - видео с pipe:0).
                // amix смешивает их в один стереопоток; duration=longest -
                // стрим не обрывается, если один из источников временно тих
                // (amix по умолчанию останавливается на самом коротком входе,
                // что для живых устройств не применимо, но указываем явно
                // для ясности и на случай различий в сборках ffmpeg).
                sb.Append("-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=0[aout]\" ");
                sb.Append("-map 0:v -map \"[aout]\" ");
                sb.Append($"-c:a aac -b:a {options.AudioBitrateKbps}k -ar 44100 ");
            }
            else if (hasMic || hasLoopback)
            {
                sb.Append("-map 0:v -map 1:a ");
                sb.Append($"-c:a aac -b:a {options.AudioBitrateKbps}k -ar 44100 ");
            }
            else
            {
                sb.Append("-an "); // без звука вообще, если ни один источник не выбран
            }

            // ---------- Выход: RTMP ----------
            sb.Append("-f flv ");
            sb.Append($"\"{options.RtmpUrl.TrimEnd('/')}/{options.StreamKey}\"");

            return sb.ToString();
        }

        private static string EscapeDshowName(string deviceName)
        {
            // Имена dshow-устройств могут содержать кавычки или обратные
            // слэши в редких случаях (драйверы с нестандартными именами) -
            // экранируем, чтобы не сломать разбор аргументов командной строки.
            return deviceName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            Stop();
            _stderrCts?.Dispose();
        }
    }

    public class FFmpegStartOptions
    {
        public required string RtmpUrl { get; init; }
        public required string StreamKey { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Fps { get; init; }
        public required int VideoBitrateKbps { get; init; }
        public required int AudioBitrateKbps { get; init; }
        public string? MicrophoneDeviceName { get; init; }
        public string? SystemAudioDeviceName { get; init; }
    }

    /// <summary>
    /// Кольцевой буфер последних N строк - для показа пользователю
    /// последних сообщений ffmpeg при ошибке, без неограниченного роста
    /// памяти на длинных стримах.
    /// </summary>
    public class RecentLinesBuffer
    {
        private readonly int _capacity;
        private readonly System.Collections.Generic.LinkedList<string> _lines = new();
        private readonly object _lock = new();

        public RecentLinesBuffer(int capacity) => _capacity = capacity;

        public void Add(string line)
        {
            lock (_lock)
            {
                _lines.AddLast(line);
                if (_lines.Count > _capacity)
                {
                    _lines.RemoveFirst();
                }
            }
        }

        public string[] Snapshot()
        {
            lock (_lock)
            {
                var arr = new string[_lines.Count];
                _lines.CopyTo(arr, 0);
                return arr;
            }
        }
    }
}
