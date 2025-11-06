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
        public string ColorValue { get; set; } = "000.000.000";
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
        private string CurrentPassword = "1234"; // Установите ваш пароль по умолчанию
        public int temp = 0;

        public MainWindow()
        {
            InitializeComponent();
            // Подписываем кнопку "Сбросить" на вызов функции Reset
            ResetButton.Click += (sender, e) => Reset();
            SaveButton.Click += (sender, e) => SaveSettings();
            ChangePasswordButton.Click += (sender, e) => OpenChangePasswordDialog();
            ColorInputBox.PreviewKeyDown += PreviewKeyDown;
            SensitivityTextBox.PreviewKeyDown += PreviewKeyDown;

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
            ColorInputBox.Text = "000.000.000"; // Базовое значение
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

        public void ColorInputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox textBox = (TextBox)sender;


            // Проверяем, не превышает ли текущий текст + новый символ лимит в 11
            if (textBox.Text.Length >= 11)
            {
                e.Handled = true; // Отменяем ввод, если уже 11 символов
                return;
            }

            // Проверяем, является ли вводимый символ цифрой, точкой или запятой
            char inputChar = e.Text[e.Text.Length - 1];
            if (!char.IsDigit(inputChar) && inputChar != '.' && inputChar != ',')
            {
                e.Handled = true; // Отменяем ввод, если символ не разрешён
                return;
            }
        }

        public void PreviewKeyDown(object sender, KeyEventArgs e)
        {
            TextBox textBox = (TextBox)sender;

            // Блокируем пробел
            if (e.Key == Key.Space)
            {
                e.Handled = true;
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
            //IsMonoColor = MonoColor.IsChecked == true;
            GradientSpeed = SpeedG.Value;

            // --- Проверка ColorValue ---
            if(IsMonoColor = MonoColor.IsChecked == true) { ColorValue = ColorInputBox.Text; }
            
           

            var colorPattern = @"^\d{3}[.,;]\d{3}[.,;]\d{3}$";
            var regex = new System.Text.RegularExpressions.Regex(colorPattern);
            if (MonoColor.IsChecked == true)
            {
                if (!regex.IsMatch(ColorValue))
                {
                    MessageBox.Show("Недопустимый формат цвета. Используйте формат: xxxyxxxyxxx (где x — цифра, y — . или ; или ,).",
                                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return; // Прерываем выполнение
                }
            }
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

        private void OpenChangePasswordDialog()
        {
            var dialog = new Window
            {
                Title = "Изменение пароля",
                Width = 400,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)), // Серый фон
                Foreground = Brushes.White
            };

            var grid = new Grid();
            grid.Margin = new Thickness(20);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Заголовок
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Старый пароль
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Новый пароль
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Кнопки

            // --- Заголовок БЕЗ ИКОНКИ ---
            var headerText = new TextBlock
            {
                Text = "Изменение пароля",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 20)
            };
            Grid.SetRow(headerText, 0);
            grid.Children.Add(headerText);

            // --- Поле "Старый пароль" с кнопкой показа ---
            var oldPasswordLabel = new Label
            {
                Content = "Старый пароль",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 5),
                Foreground = Brushes.White
            };
            var oldPasswordStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var oldPasswordBox = new PasswordBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                PasswordChar = '*'
            };
            var oldPasswordTextBox = new TextBox // Новое поле для показа текста
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Text = "",
                Visibility = Visibility.Collapsed // Скрыто по умолчанию
            };
            var showOldButton = new Button
            {
                Content = "👁️",
                Width = 30,
                Height = 25,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Gray,
                Cursor = Cursors.Hand,
                Padding = new Thickness(0)
            };
            // Стиль кнопки: синий при наведении
            showOldButton.MouseEnter += (s, e) => showOldButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            showOldButton.MouseLeave += (s, e) => showOldButton.Background = Brushes.Transparent;

            // При нажатии — показываем текст в новом поле
            showOldButton.PreviewMouseDown += (s, e) =>
            {
                oldPasswordTextBox.Text = oldPasswordBox.Password;
                oldPasswordBox.Visibility = Visibility.Collapsed;
                oldPasswordTextBox.Visibility = Visibility.Visible;
            };
            // При отпускании — скрываем новое поле и возвращаем маскировку
            showOldButton.PreviewMouseUp += (s, e) =>
            {
                oldPasswordTextBox.Visibility = Visibility.Collapsed;
                oldPasswordBox.Visibility = Visibility.Visible;
            };
            // Если курсор ушёл, а кнопка всё ещё нажата — скрываем
            showOldButton.LostMouseCapture += (s, e) =>
            {
                oldPasswordTextBox.Visibility = Visibility.Collapsed;
                oldPasswordBox.Visibility = Visibility.Visible;
            };

            oldPasswordStack.Children.Add(oldPasswordBox);
            oldPasswordStack.Children.Add(oldPasswordTextBox);
            oldPasswordStack.Children.Add(showOldButton);
            var oldPasswordContainer = new StackPanel();
            oldPasswordContainer.Children.Add(oldPasswordLabel);
            oldPasswordContainer.Children.Add(oldPasswordStack);
            Grid.SetRow(oldPasswordContainer, 1);
            grid.Children.Add(oldPasswordContainer);

            // --- Поле "Новый пароль" с кнопкой показа ---
            var newPasswordLabel = new Label
            {
                Content = "Новый пароль",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 5),
                Foreground = Brushes.White
            };
            var newPasswordStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
            var newPasswordBox = new PasswordBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                PasswordChar = '*'
            };
            var newPasswordTextBox = new TextBox // Новое поле для показа текста
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Text = "",
                Visibility = Visibility.Collapsed // Скрыто по умолчанию
            };
            var showNewButton = new Button
            {
                Content = "👁️",
                Width = 30,
                Height = 25,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Gray,
                Cursor = Cursors.Hand,
                Padding = new Thickness(0)
            };
            // Стиль кнопки: синий при наведении
            showNewButton.MouseEnter += (s, e) => showNewButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            showNewButton.MouseLeave += (s, e) => showNewButton.Background = Brushes.Transparent;

            // При нажатии — показываем текст в новом поле
            showNewButton.PreviewMouseDown += (s, e) =>
            {
                newPasswordTextBox.Text = newPasswordBox.Password;
                newPasswordBox.Visibility = Visibility.Collapsed;
                newPasswordTextBox.Visibility = Visibility.Visible;
            };
            // При отпускании — скрываем новое поле и возвращаем маскировку
            showNewButton.PreviewMouseUp += (s, e) =>
            {
                newPasswordTextBox.Visibility = Visibility.Collapsed;
                newPasswordBox.Visibility = Visibility.Visible;
            };
            // Если курсор ушёл, а кнопка всё ещё нажата — скрываем
            showNewButton.LostMouseCapture += (s, e) =>
            {
                newPasswordTextBox.Visibility = Visibility.Collapsed;
                newPasswordBox.Visibility = Visibility.Visible;
            };

            newPasswordStack.Children.Add(newPasswordBox);
            newPasswordStack.Children.Add(newPasswordTextBox);
            newPasswordStack.Children.Add(showNewButton);
            var newPasswordContainer = new StackPanel();
            newPasswordContainer.Children.Add(newPasswordLabel);
            newPasswordContainer.Children.Add(newPasswordStack);
            Grid.SetRow(newPasswordContainer, 2);
            grid.Children.Add(newPasswordContainer);

            // --- Кнопки "Далее" и "Отмена" ---
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            // Стиль для всех кнопок
            Style buttonStyle = new Style(typeof(Button));
            buttonStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.White));
            buttonStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Black));
            buttonStyle.Setters.Add(new Setter(Button.BorderBrushProperty, Brushes.Gray));
            buttonStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            buttonStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 6, 12, 6)));
            buttonStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0)); 
            buttonStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 80.0));  

            // Триггер: при наведении — синий фон
            buttonStyle.Triggers.Add(new Trigger
            {
                Property = Button.IsMouseOverProperty,
                Value = true,
                Setters =
        {
            new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0, 120, 212))),
            new Setter(Button.ForegroundProperty, Brushes.White)
        }
            });

            var nextButton = new Button { Content = "Сохранить", Style = buttonStyle };
            var cancelButton = new Button { Content = "Отмена", Style = buttonStyle };

            buttonStack.Children.Add(nextButton);
            buttonStack.Children.Add(cancelButton);
            Grid.SetRow(buttonStack, 3);
            grid.Children.Add(buttonStack);

            // --- Обработчик кнопки "Далее" ---
            nextButton.Click += (s, e) =>
            {
                //// Всегда показываем введённые пароли
                //MessageBox.Show($"Старый пароль: {oldPasswordBox.Password}\nНовый пароль: {newPasswordBox.Password}",
                //                "Введённые данные", MessageBoxButton.OK, MessageBoxImage.Information);

                // Проверяем, совпадает ли старый пароль
                if (oldPasswordBox.Password == CurrentPassword)
                {
                    if (!string.IsNullOrEmpty(newPasswordBox.Password))
                    {
                        CurrentPassword = newPasswordBox.Password;
                        MessageBox.Show("Пароль успешно изменён!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                        dialog.Close();
                    }
                    else
                    {
                        MessageBox.Show("Новый пароль не может быть пустым.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    temp++;

                    MessageBox.Show("Пароль успешно не изменён!", $"Количество попыток {3 - temp}", MessageBoxButton.OK, MessageBoxImage.Information);

                    if (temp == 3)
                    {

                        ShowLockoutDialog();
                        
                        temp = 0;   
                    }

                }
            };

            // --- Обработчик кнопки "Отмена" ---
            cancelButton.Click += (s, e) => dialog.Close();

            dialog.Content = grid;
            dialog.ShowDialog();
        }

        public void ShowLockoutDialog()
        {
            var lockoutDialog = new Window
            {
                Title = "Приложение заблокировано",
                Width = 350,
                Height = 150,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White
            };

            var textBlock = new TextBlock
            {
                Text = "Приложение заблокировано на 1 минуту.",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 16,
                Margin = new Thickness(10)
            };

            var countdownText = new TextBlock
            {
                Text = "60",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 16,
                Margin = new Thickness(10, 40, 10, 10)
            };

            var stack = new StackPanel();
            stack.Children.Add(textBlock);
            stack.Children.Add(countdownText);

            lockoutDialog.Content = stack;

            // Таймер обратного отсчёта
            var startTime = DateTime.Now;
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += (s, e) =>
            {
                var elapsed = (DateTime.Now - startTime).TotalSeconds;
                var remaining = Math.Max(0, 60 - (int)elapsed);
                countdownText.Text = remaining.ToString();

                if (remaining <= 0)
                {
                    timer.Stop();
                    lockoutDialog.Close();
                }
            };
            timer.Start();

            // Запрещаем закрытие окна вручную
            lockoutDialog.Closing += (s, e) =>
            {
                if ((DateTime.Now - startTime).TotalSeconds < 60)
                {
                    e.Cancel = true; // Отменяем закрытие
                }
            };

            lockoutDialog.ShowDialog();
        }

        
    }
}