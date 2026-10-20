using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Список имён dshow-устройств известен самому ffmpeg - он печатает
    /// список в stderr при запуске с "-list_devices true -f dshow -i dummy"
    /// (ровно так же, как это делает командная строка вручную - см.
    /// FFmpegEncoder, где для захвата уже используется -f dshow). Отдельного
    /// API перечисления устройств в ffmpeg нет - приходится парсить stderr.
    /// </summary>
    public static class AudioDeviceEnumerator
    {
        private static readonly Regex DeviceLineRegex = new(
            @"^\[dshow[^\]]*\]\s+""(?<name>.+)""\s*$", RegexOptions.Compiled);

        public static async Task<AudioDeviceList> ListAsync()
        {
            string? ffmpegPath = FFmpegLocator.Find();
            if (ffmpegPath == null)
            {
                return new AudioDeviceList(new List<string>(), "ffmpeg.exe не найден");
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return new AudioDeviceList(new List<string>(), "Не удалось запустить ffmpeg");
            }

            string stderrOutput = await process.StandardError.ReadToEndAsync();
            // ffmpeg со списком устройств всегда завершается ненулевым кодом
            // (потому что "dummy" - не настоящий входной файл) - это ожидаемо,
            // не признак ошибки, поэтому код возврата здесь не проверяется.
            process.WaitForExit(5000);

            return ParseDeviceList(stderrOutput);
        }

        internal static AudioDeviceList ParseDeviceList(string ffmpegStderr)
        {
            var devices = new List<string>();
            bool inAudioSection = false;

            foreach (string rawLine in ffmpegStderr.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');

                if (line.Contains("DirectShow audio devices"))
                {
                    inAudioSection = true;
                    continue;
                }
                if (line.Contains("DirectShow video devices"))
                {
                    inAudioSection = false;
                    continue;
                }

                if (inAudioSection)
                {
                    Match match = DeviceLineRegex.Match(line.Trim());
                    if (match.Success)
                    {
                        devices.Add(match.Groups["name"].Value);
                    }
                }
            }

            return new AudioDeviceList(devices, null);
        }

        /// <summary>
        /// Эвристика: имена устройств, которые типично означают loopback
        /// системного звука, а не микрофон - используется только чтобы
        /// предложить разумный выбор по умолчанию в UI, не как единственный
        /// способ выбора (пользователь всегда может выбрать вручную из
        /// полного списка).
        /// </summary>
        public static bool LooksLikeSystemAudioDevice(string deviceName)
        {
            string lower = deviceName.ToLowerInvariant();
            return lower.Contains("stereo mix") ||
                   lower.Contains("what u hear") ||
                   lower.Contains("loopback") ||
                   lower.Contains("virtual-audio-capturer") ||
                   lower.Contains("cable output") || // VB-Audio Virtual Cable
                   lower.Contains("vb-audio");
        }
    }

    public class AudioDeviceList
    {
        public IReadOnlyList<string> DeviceNames { get; }
        public string? Error { get; }

        public AudioDeviceList(IReadOnlyList<string> deviceNames, string? error)
        {
            DeviceNames = deviceNames;
            Error = error;
        }
    }
}
