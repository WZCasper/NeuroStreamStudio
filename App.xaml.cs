using System;
using System.Windows;
using System.Windows.Threading;

namespace NeuroStreamStudio
{
    public partial class App : Application
    {
        public App()
        {
            // Глобальный перехват необработанных исключений в UI-потоке -
            // без этого необработанное исключение закрывает приложение
            // молча или с низкоуровневым окном Windows "программа
            // перестала работать", без понятного пользователю сообщения.
            // Особенно важно здесь: оборванная трансляция посреди стрима
            // без объяснения причины - худший возможный опыт для живого
            // эфира, лучше показать, что пошло не так.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Произошла непредвиденная ошибка:\n\n{e.Exception.Message}\n\n" +
                "Если трансляция была активна, она могла прерваться.",
                "NeuroStreamStudio — ошибка",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
