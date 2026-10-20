using System;
using System.IO;
using System.Text.Json;

namespace NeuroStreamStudio
{
    /// <summary>
    /// Настройки стрима, сохраняются в %APPDATA%\NeuroStreamStudio\settings.json.
    /// RtmpUrl и StreamKey хранятся раздельно (как в самом TikTok Live Center/
    /// Twitch/YouTube: "Server URL" и "Stream Key" - два разных поля), а не
    /// склеенными в одну строку - это соответствует тому, как площадки сами
    /// показывают эти значения, и упрощает валидацию каждого поля отдельно.
    ///
    /// StreamKey — чувствительные данные (TikTok-ключ истекает каждые ~2 часа
    /// и выдаётся заново при каждой сессии, но сам факт хранения в открытом
    /// файле на диске всё равно нежелателен для постоянных ключей других
    /// площадок) - хранится отдельно в зашифрованном виде через
    /// System.Security.Cryptography.ProtectedData (DPAPI, привязано к
    /// текущему пользователю Windows), а не в самом settings.json.
    /// </summary>
    public class StreamSettings
    {
        public string RtmpUrl { get; set; } = "";

        /// <summary>
        /// Зашифрованный DPAPI StreamKey в Base64. Не читайте/пишите это
        /// поле напрямую - используйте StreamKeyPlainText (get/set) ниже,
        /// которое шифрует/расшифровывает прозрачно.
        /// </summary>
        public string StreamKeyEncrypted { get; set; } = "";

        public int VideoBitrateKbps { get; set; } = 6000;
        public int AudioBitrateKbps { get; set; } = 160;
        public string Resolution { get; set; } = "1920x1080";
        public int Fps { get; set; } = 30;

        public int SelectedScreenIndex { get; set; } = 0;
        public string SelectedCameraDeviceId { get; set; } = "";

        // Позиция и размер окна камеры (picture-in-picture) в процентах
        // от холста - согласовано с тем же подходом, что уже использует
        // раскладка виджетов в NeuroOverlay (left/top/width/height в %),
        // чтобы при экспорте пресетов между проектами формат совпадал.
        public double CameraLeftPercent { get; set; } = 70;
        public double CameraTopPercent { get; set; } = 65;
        public double CameraWidthPercent { get; set; } = 25;

        [System.Text.Json.Serialization.JsonIgnore]
        public string StreamKeyPlainText
        {
            get => DecryptStreamKey(StreamKeyEncrypted);
            set => StreamKeyEncrypted = EncryptStreamKey(value);
        }

        private static string EncryptStreamKey(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return "";
            }

            try
            {
                byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    plainBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encryptedBytes);
            }
            catch
            {
                // DPAPI недоступен в редких средах (например, служебная учётная
                // запись без профиля) - в этом случае лучше не падать, а просто
                // не шифровать, чем потерять ключ пользователя без объяснения.
                return plainText;
            }
        }

        private static string DecryptStreamKey(string encrypted)
        {
            if (string.IsNullOrEmpty(encrypted))
            {
                return "";
            }

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encrypted);
                byte[] plainBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    encryptedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // Либо значение не было зашифровано (см. фолбэк выше), либо
                // зашифровано другим пользователем Windows - в обоих случаях
                // возвращаем как есть, а не бросаем исключение при загрузке
                // настроек.
                return encrypted;
            }
        }

        private static string GetSettingsPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NeuroStreamStudio");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }

        public static StreamSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<StreamSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch
            {
                // Повреждённый или нечитаемый файл настроек - используем
                // значения по умолчанию вместо падения при запуске.
            }

            return new StreamSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // Не удалось сохранить - не критично для текущего сеанса,
                // просто настройки не переживут перезапуск.
            }
        }
    }
}
