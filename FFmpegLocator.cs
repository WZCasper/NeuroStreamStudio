using System;
using System.Diagnostics;
using System.IO;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Находит ffmpeg.exe для запуска кодирования/RTMP-пуша.
    ///
    /// ffmpeg.exe не является NuGet-пакетом и не встроен в .csproj -
    /// это отдельный исполняемый файл, который CI (.github/workflows/build.yml)
    /// скачивает с gyan.dev (официально рекомендованная сборка на
    /// ffmpeg.org/download.html) и кладёт рядом с NeuroStreamStudio.exe при
    /// публикации. Так NeuroStreamStudio.exe остаётся download-and-run без
    /// отдельной установки FFmpeg пользователем - то же решение, которое
    /// уже использует TwitchDownloaderWPF и другие .NET-приложения с
    /// похожей задачей.
    /// </summary>
    public static class FFmpegLocator
    {
        /// <summary>
        /// Путь, где CI кладёт ffmpeg.exe при публикации - рядом с самим
        /// NeuroStreamStudio.exe. Это первое и основное место поиска.
        /// </summary>
        public static string BundledPath =>
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        /// <summary>
        /// Пытается найти рабочий ffmpeg.exe: сначала рядом с приложением
        /// (BundledPath), затем в PATH системы (если пользователь уже
        /// поставил FFmpeg сам - например, через winget). Возвращает null,
        /// если ничего не найдено - вызывающий код должен сообщить
        /// пользователю понятное сообщение, а не упасть с низкоуровневой
        /// ошибкой запуска процесса.
        /// </summary>
        public static string? Find()
        {
            if (File.Exists(BundledPath))
            {
                return BundledPath;
            }

            string? fromPath = FindInSystemPath();
            if (fromPath != null)
            {
                return fromPath;
            }

            return null;
        }

        private static string? FindInSystemPath()
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return null;
            }

            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Некорректная запись в PATH (пустая строка, недопустимые
                    // символы) - пропускаем и проверяем следующую.
                }
            }

            return null;
        }

        /// <summary>
        /// Запускает найденный ffmpeg с "-version" и проверяет, что процесс
        /// действительно отвечает - на случай повреждённого или
        /// несовместимого исполняемого файла, который формально существует
        /// на диске, но не запускается.
        /// </summary>
        public static bool VerifyWorks(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return false;
                }

                bool exited = process.WaitForExit(5000);
                if (!exited)
                {
                    try { process.Kill(); } catch { /* процесс уже мог завершиться сам */ }
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
