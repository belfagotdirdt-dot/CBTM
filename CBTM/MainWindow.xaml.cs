using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CBTM
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {

        // --- Переменные для хранения значений из XAML ---
        public bool InvertX { get; set; }
        public bool InvertY { get; set; }
        public string Sensitivity { get; set; } = "1";

        public double Brightness { get; set; } = 50;
        public bool IsGradient { get; set; } = true;
        public bool IsMonoColor { get; set; } = false;
        public string ColorValue { get; set; } = "RGB";
        public double GradientSpeed { get; set; } = 50;

        public string M1Value { get; set; } = "left click";
        public string M2Value { get; set; } = "right click";
        public string M3Value { get; set; } = "middle click";
        public string M4Value { get; set; } = "back";
        public string M5Value { get; set; } = "forward";
        public string M6Value { get; set; } = "Volume Up";
        public string M7Value { get; set; } = "Volume Down";
        public string M8Value { get; set; } = "Mute";
        public string M9Value { get; set; } = "Task View";

        // --- Переменная для хранения текущего пароля ---
        private string CurrentPassword = "M1M1M1M1M1M1"; // Пример: текущий пароль — 6 символов из кнопок

        public MainWindow()
        {
            InitializeComponent();
            // Подписываем кнопку "Сбросить" на вызов функции Reset
            ResetButton.Click += (sender, e) => Reset();
            SaveButton.Click += (sender, e) => SaveSettings();
           
        }

        public void Reset()
        {
            // --- Курсор ---
            InvertXCheckbox.IsChecked = false;
            InvertYCheckbox.IsChecked = false;
            SensitivityTextBox.Text = "1";

            // --- Подсветка ---
            BrightnessSlider.Value = 50;
            Gradient.IsChecked = true;  // Устанавливаем "Градиент" как активный
            MonoColor.IsChecked = false; // "Моноцвет" неактивен
            ColorInputBox.Text = "RGB"; // Базовое значение
            SpeedG.Value = 50;

            // --- Клавиши ---
            M1.Text = "left click";
            M2.Text = "right click";
            M3.Text = "middle click";
            M4.Text = "back";
            M5.Text = "forward";
            M6.Text = "Volume Up";
            M7.Text = "Volume Down";
            M8.Text = "Mute";
            M9.Text = "Task View";
        }

        public void SensitivityTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox textBox = (TextBox)sender;

            // Проверяем, не превышает ли текущий текст + новый символ лимит в 4
            if (textBox.Text.Length >= 4)
            {
                e.Handled = true; // Отменяем ввод, если уже 4 символа
                return;
            }

            // Проверяем, является ли вводимый символ цифрой
            if (!char.IsDigit(e.Text, e.Text.Length - 1))
            {
                e.Handled = true; // Отменяем ввод, если не цифра
                return;
            }
        }

        public void SaveSettings()
        {
            // --- Курсор ---
            InvertX = InvertXCheckbox.IsChecked == true;
            InvertY = InvertYCheckbox.IsChecked == true;
            Sensitivity = SensitivityTextBox.Text;

            // --- Подсветка ---
            Brightness = BrightnessSlider.Value;
            IsGradient = Gradient.IsChecked == true;
            IsMonoColor = MonoColor.IsChecked == true;
            ColorValue = ColorInputBox.Text;
            GradientSpeed = SpeedG.Value;

            // --- Клавиши ---
            M1Value = M1.Text;
            M2Value = M2.Text;
            M3Value = M3.Text;
            M4Value = M4.Text;
            M5Value = M5.Text;
            M6Value = M6.Text;
            M7Value = M7.Text;
            M8Value = M8.Text;
            M9Value = M9.Text;

            //// --- Вывод в терминал ---
            //Debug.WriteLine("=== Настройки сохранены ===");
            //Debug.WriteLine($"Инверсия X: {InvertX}");
            //Debug.WriteLine($"Инверсия Y: {InvertY}");
            //Debug.WriteLine($"Чувствительность: {Sensitivity}");
            //Debug.WriteLine($"Яркость: {Brightness}");
            //Debug.WriteLine($"Градиент: {IsGradient}");
            //Debug.WriteLine($"Моноцвет: {IsMonoColor}");
            //Debug.WriteLine($"Цвет: {ColorValue}");
            //Debug.WriteLine($"Скорость градиента: {GradientSpeed}");
            //Debug.WriteLine($"M1: {M1Value}");
            //Debug.WriteLine($"M2: {M2Value}");
            //Debug.WriteLine($"M3: {M3Value}");
            //Debug.WriteLine($"M4: {M4Value}");
            //Debug.WriteLine($"M5: {M5Value}");
            //Debug.WriteLine($"M6: {M6Value}");
            //Debug.WriteLine($"M7: {M7Value}");
            //Debug.WriteLine($"M8: {M8Value}");
            //Debug.WriteLine($"M9: {M9Value}");
            //Debug.WriteLine("========================");

            // Пример: показываем сообщение
           
        }


        private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            // Создаём окно с полями ввода
            Window passwordWindow = new Window()
            {
                Title = "Изменить пароль",
                Width = 350,
                Height = 200,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Создаём StackPanel для размещения элементов
            StackPanel panel = new StackPanel()
            {
                Margin = new Thickness(10)
            };

            // Создаём текстовые блоки и поля ввода
            Label labelOldPassword = new Label() { Content = "Введите старый пароль:" };
            PasswordBox inputOldPassword = new PasswordBox() { Margin = new Thickness(0, 0, 0, 10) };

            Label labelOldPasswordConfirm = new Label() { Content = "Повторите старый пароль:" };
            PasswordBox inputOldPasswordConfirm = new PasswordBox() { Margin = new Thickness(0, 0, 0, 10) };

            Label labelNewPassword = new Label() { Content = "Введите новый пароль:" };
            PasswordBox inputNewPassword = new PasswordBox() { Margin = new Thickness(0, 0, 0, 10) };

            // Кнопка подтверждения
            Button submitButton = new Button()
            {
                Content = "Сменить пароль",
                Margin = new Thickness(0, 10, 0, 0)
            };

            // Добавляем элементы в панель
            panel.Children.Add(labelOldPassword);
            panel.Children.Add(inputOldPassword);
            panel.Children.Add(labelOldPasswordConfirm);
            panel.Children.Add(inputOldPasswordConfirm);
            panel.Children.Add(labelNewPassword);
            panel.Children.Add(inputNewPassword);
            panel.Children.Add(submitButton);

            // Обработчик нажатия кнопки
            submitButton.Click += (s, args) =>
            {
                string oldPass = inputOldPassword.Password;
                string oldPassConfirm = inputOldPasswordConfirm.Password;
                string newPass = inputNewPassword.Password;

                // Проверка: 6 значений, только кнопки мыши
                if (!IsValidPassword(oldPass) || !IsValidPassword(oldPassConfirm) || !IsValidPassword(newPass))
                {
                    MessageBox.Show("Пароль должен состоять из 6 значений (например: M1M2M3).", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (oldPass != CurrentPassword)
                {
                    MessageBox.Show("Старый пароль неверен.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (oldPass != oldPassConfirm)
                {
                    MessageBox.Show("Повтор старого пароля не совпадает.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CurrentPassword = newPass;
                MessageBox.Show("Пароль успешно изменён!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                passwordWindow.Close();
            };

            passwordWindow.Content = panel;
            passwordWindow.ShowDialog();
        }

        /// <summary>
        /// Проверяет, является ли строка допустимым паролем (6 значений, только кнопки мыши).
        /// Пример: M1M2M3, LRM1M2, M3M4M5 и т.д.
        /// </summary>
        private bool IsValidPassword(string password)
        {
            if (password.Length != 6)
                return false;

            // Регулярное выражение для проверки: M1-M9, L, R, M
            Regex regex = new Regex(@"^(M[1-9]|[LRM]){6}$", RegexOptions.IgnoreCase);
            return regex.IsMatch(password);
        }


    }
}