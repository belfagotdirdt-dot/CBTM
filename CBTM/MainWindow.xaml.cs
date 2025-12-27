using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CBTM
{
    public partial class MainWindow : Window
    {
        public struct MouseSettings
        {
            public bool InvertX { get; set; }
            public bool InvertY { get; set; }
            public string Sensitivity { get; set; }
            public double Brightness { get; set; }
            public bool IsGradient { get; set; }
            public bool IsMonoColor { get; set; }
            public string ColorValue { get; set; }
            public double GradientSpeed { get; set; }
            public string M1Value { get; set; }
            public string M2Value { get; set; }
            public string M3Value { get; set; }
            public string M4Value { get; set; }
            public string M5Value { get; set; }
            public string M6Value { get; set; }
            public string M7Value { get; set; }
            public string M8Value { get; set; }
            public string M9Value { get; set; }
            public string CurrentPassword { get; set; }
            public SerialPort SerialPort { get; set; }
            public string SelectedPortName { get; set; }

            public MouseSettings()
            {
                InvertX = false;
                InvertY = false;
                Sensitivity = "1";
                Brightness = 50;
                IsGradient = true;
                IsMonoColor = false;
                ColorValue = "000.000.000";
                GradientSpeed = 50;
                M1Value = "left click";
                M2Value = "right click";
                M3Value = "middle click";
                M4Value = "back";
                M5Value = "forward";
                M6Value = "Volume Up";
                M7Value = "Volume Down";
                M8Value = "Mute";
                M9Value = "Task View";
                CurrentPassword = "1234";
                SerialPort = null;
                SelectedPortName = "";
            }
        }

        private MouseSettings Settings = new MouseSettings();
        public int temp = 0;

        public MainWindow()
        {
            InitializeComponent();
            Debug.WriteLine("op");
            ResetButton.Click += (sender, e) => RequestSettingsFromArduino();
            SaveButton.Click += (sender, e) => SaveSettings();
            ChangePasswordButton.Click += (sender, e) => OpenChangePasswordDialog();

            ColorInputBox.PreviewTextInput += ColorInputBox_PreviewTextInput;
            SensitivityTextBox.PreviewTextInput += SensitivityTextBox_PreviewTextInput;

            LoadAvailablePorts();

            PortComboBox.SelectionChanged += (s, e) => SelectComPortFromComboBox();
            PortComboBox.DropDownOpened += (s, e) => LoadAvailablePorts();

            Gradient.Checked += (s, e) => SpeedG.Background = new SolidColorBrush(Color.FromRgb(34, 34, 34));
            MonoColor.Checked += (s, e) => SpeedG.Background = new SolidColorBrush(Color.FromRgb(100, 100, 100));
        }

        private void RequestSettingsFromArduino()
        {
            if (Settings.SerialPort == null || !Settings.SerialPort.IsOpen)
            {
                MessageBox.Show("COM-порт не подключен.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Debug.WriteLine("=== Запрос настроек ===");

                // Очистка буферов
                Settings.SerialPort.DiscardInBuffer();
                Settings.SerialPort.DiscardOutBuffer();
                System.Threading.Thread.Sleep(100);

                // Отправка команды
                Settings.SerialPort.WriteLine("GET_SETTINGS");
                Debug.WriteLine("Отправлено: GET_SETTINGS");

                // Чтение ответа
                string response = ReadSerialResponse(3000);

                if (!string.IsNullOrEmpty(response))
                {
                    UpdateSettingsFromArduino(response);
                }
                else
                {
                    MessageBox.Show("Не удалось получить ответ от Arduino. Проверьте соединение.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ReadSerialResponse(int timeoutMs)
        {
            StringBuilder responseBuilder = new StringBuilder();
            DateTime startTime = DateTime.Now;

            try
            {
                Settings.SerialPort.ReadTimeout = timeoutMs;

                while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
                {
                    if (Settings.SerialPort.BytesToRead > 0)
                    {
                        try
                        {
                            string line = Settings.SerialPort.ReadLine().TrimEnd('\r', '\n');
                            Debug.WriteLine($"Получено: '{line}'");

                            // Принимаем все ответы, кроме пустых
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                // Если строка содержит настройки (есть запятые)
                                if (line.Contains(",") && line.Length > 10)
                                {
                                    return line;
                                }

                                // Или это подтверждение OK
                                if (line.Contains("OK") || line.Contains("PONG") || line.Contains("READY"))
                                {
                                    return line;
                                }

                                responseBuilder.AppendLine(line);
                            }
                        }
                        catch (TimeoutException) { }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка чтения строки: {ex.Message}");
                        }
                    }
                    System.Threading.Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка в ReadSerialResponse: {ex.Message}");
            }

            return responseBuilder.Length > 0 ? responseBuilder.ToString().Trim() : null;
        }

        private void UpdateSettingsFromArduino(string data)
        {
            try
            {
                Debug.WriteLine($"Обработка данных: {data}");
                string[] parts = data.Split(',');

                if (parts.Length >= 9)
                {
                    Settings.InvertX = parts[0] == "1";
                    Settings.InvertY = parts[1] == "1";
                    Settings.Sensitivity = parts[2];
                    Settings.Brightness = double.Parse(parts[3]);
                    Settings.IsGradient = parts[4] == "1";
                    Settings.IsMonoColor = parts[5] == "1";
                    Settings.ColorValue = parts[6];
                    Settings.GradientSpeed = double.Parse(parts[7]);

                    Dispatcher.Invoke(() =>
                    {
                        InvertXCheckbox.IsChecked = Settings.InvertX;
                        InvertYCheckbox.IsChecked = Settings.InvertY;
                        SensitivityTextBox.Text = Settings.Sensitivity;
                        BrightnessSlider.Value = Settings.Brightness;
                        Gradient.IsChecked = Settings.IsGradient;
                        MonoColor.IsChecked = Settings.IsMonoColor;
                        ColorInputBox.Text = Settings.ColorValue;
                        SpeedG.Value = Settings.GradientSpeed;

                        if (parts.Length > 8) { Settings.M1Value = parts[8]; M1.Text = parts[8]; }
                        if (parts.Length > 9) { Settings.M2Value = parts[9]; M2.Text = parts[9]; }
                        if (parts.Length > 10) { Settings.M3Value = parts[10]; M3.Text = parts[10]; }
                        if (parts.Length > 11) { Settings.M4Value = parts[11]; M4.Text = parts[11]; }
                        if (parts.Length > 12) { Settings.M5Value = parts[12]; M5.Text = parts[12]; }
                        if (parts.Length > 13) { Settings.M6Value = parts[13]; M6.Text = parts[13]; }
                        if (parts.Length > 14) { Settings.M7Value = parts[14]; M7.Text = parts[14]; }
                        if (parts.Length > 15) { Settings.M8Value = parts[15]; M8.Text = parts[15]; }
                        if (parts.Length > 16) { Settings.M9Value = parts[16]; M9.Text = parts[16]; }
                        if (parts.Length > 17) { Settings.CurrentPassword = parts[17]; }
                    });

                    MessageBox.Show("Настройки успешно загружены с Arduino!", "Успех",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Неверный формат данных. Получено {parts.Length} полей, нужно минимум 9.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка обработки данных: {ex.Message}\n\nДанные: {data}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SendSettingsToArduino()
        {
            try
            {
                if (Settings.SerialPort == null || !Settings.SerialPort.IsOpen)
                {
                    MessageBox.Show("COM-порт не подключен.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string data = BuildSettingsString();
                Debug.WriteLine($"Отправка настроек: {data}");

                Settings.SerialPort.WriteLine(data);

                System.Threading.Thread.Sleep(200);
                string response = ReadSerialResponse(2000);

                if (!string.IsNullOrEmpty(response) && (response.Contains("OK") || response.Contains("DEBUG")))
                {
                    MessageBox.Show("Настройки успешно отправлены на Arduino!", "Успех",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Настройки отправлены, но подтверждение не получено.",
                        "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка отправки: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildSettingsString()
        {
            return $"{(Settings.InvertX ? "1" : "0")}," +
                   $"{(Settings.InvertY ? "1" : "0")}," +
                   $"{Settings.Sensitivity}," +
                   $"{Settings.Brightness}," +
                   $"{(Settings.IsGradient ? "1" : "0")}," +
                   $"{(Settings.IsMonoColor ? "1" : "0")}," +
                   $"{Settings.ColorValue}," +
                   $"{Settings.GradientSpeed}," +
                   $"{Settings.M1Value}," +
                   $"{Settings.M2Value}," +
                   $"{Settings.M3Value}," +
                   $"{Settings.M4Value}," +
                   $"{Settings.M5Value}," +
                   $"{Settings.M6Value}," +
                   $"{Settings.M7Value}," +
                   $"{Settings.M8Value}," +
                   $"{Settings.M9Value}," +
                   $"{Settings.CurrentPassword}";
        }

        private bool ValidateColorFormat(string colorValue)
        {
            var colorPattern = @"^\d{3}[.,;]\d{3}[.,;]\d{3}$";
            var regex = new Regex(colorPattern);
            return regex.IsMatch(colorValue);
        }

        private void SelectComPortFromComboBox()
        {
            if (PortComboBox.SelectedItem == null ||
                PortComboBox.SelectedItem.ToString() == "не выбран")
            {
                if (Settings.SerialPort != null && Settings.SerialPort.IsOpen)
                {
                    Console.WriteLine("EEEEE " + Settings.SerialPort.PortName);
                    Settings.SerialPort.Close();
                    Settings.SerialPort = null;
                }
                Settings.SelectedPortName = "";
                return;
            }

            string selectedPort = PortComboBox.SelectedItem.ToString();

            try
            {
                if (Settings.SerialPort != null && Settings.SerialPort.IsOpen)
                {
                    Settings.SerialPort.Close();
                }

                Settings.SerialPort = new SerialPort(selectedPort, 9600)
                {
                    ReadTimeout = 3000,
                    WriteTimeout = 3000,
                    NewLine = "\n",
                    Parity = Parity.None,
                    DataBits = 8,
                    StopBits = StopBits.One,
                    Handshake = Handshake.None
                };

                Settings.SerialPort.Open();

                if (!Settings.SerialPort.IsOpen)
                {
                    MessageBox.Show($"Не удалось открыть порт {selectedPort}.", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Settings.SelectedPortName = selectedPort;

                // Даем Arduino время на инициализацию
                System.Threading.Thread.Sleep(3000);

                // Очистка буферов
                Settings.SerialPort.DiscardInBuffer();
                Settings.SerialPort.DiscardOutBuffer();

                // Проверяем соединение
                if (TestArduinoConnection())
                {
                    MessageBox.Show($"Успешно подключено к {selectedPort}", "Подключение",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Подключено к {selectedPort}, но Arduino не отвечает.\nПроверьте код на Arduino.",
                        "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения к {selectedPort}: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                PortComboBox.SelectedIndex = 0;
            }
        }

        private bool TestArduinoConnection()
        {
            try
            {
                if (Settings.SerialPort == null || !Settings.SerialPort.IsOpen)
                
                    return false;
                Console.WriteLine("sadassad");

                Debug.WriteLine("=== Тестирование связи с Arduino ===");

                // Очистка буферов
                Settings.SerialPort.DiscardInBuffer();
                Settings.SerialPort.DiscardOutBuffer();
                System.Threading.Thread.Sleep(100);

                // Отправка команды PING
                Settings.SerialPort.WriteLine("PING");
                Debug.WriteLine("Отправлено: PING");

                // Ожидание ответа
                DateTime start = DateTime.Now;
                while ((DateTime.Now - start).TotalMilliseconds < 2000)
                {
                    if (Settings.SerialPort.BytesToRead > 0)
                    {
                        try
                        {
                            string response = Settings.SerialPort.ReadLine();
                            response = response.TrimEnd('\r', '\n');
                            Debug.WriteLine($"Получен ответ: '{response}'");
                
                            if (response.Contains("PONG") ||
                                response.Contains("READY") ||
                                response.Contains("DEBUG") ||
                                response.Contains("STATUS:"))
                            {
                                Debug.WriteLine("Связь подтверждена!");
                                break;
                            }
                        }
                        catch (TimeoutException)
                        {
                            continue;
                        }
                    }
                    System.Threading.Thread.Sleep(10);
                }

                Settings.SerialPort.DiscardInBuffer();
                Settings.SerialPort.DiscardOutBuffer();
                System.Threading.Thread.Sleep(100);

                // Отправка команды
                Settings.SerialPort.WriteLine("GET_SETTINGS");
                Debug.WriteLine("Отправлено: GET_SETTINGS");

                // Чтение ответа
                string readSerialResponse = ReadSerialResponse(3000);

                if (!string.IsNullOrEmpty(readSerialResponse))
                {
                    UpdateSettingsFromArduino(readSerialResponse);
                    return true;
                }
                
                Debug.WriteLine("Таймаут ожидания ответа");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка тестирования связи: {ex.Message}");
                return false;
            }
        }

        public void SensitivityTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox textBox = (TextBox)sender;

            if (textBox.Text.Length >= 4)
            {
                e.Handled = true;
                return;
            }

            if (!char.IsDigit(e.Text, e.Text.Length - 1))
            {
                e.Handled = true;
                return;
            }
        }

        public void ColorInputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            TextBox textBox = (TextBox)sender;

            if (textBox.Text.Length >= 11)
            {
                e.Handled = true;
                return;
            }

            char inputChar = e.Text[e.Text.Length - 1];
            if (!char.IsDigit(inputChar) && inputChar != '.' && inputChar != ',')
            {
                e.Handled = true;
                return;
            }
        }

        public void PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
            }
        }

        private void OpenChangePasswordDialog()
        {
            int temp = 0; // Добавляем счетчик попыток

            var dialog = new Window
            {
                Title = "Изменение пароля",
                Width = 400,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = Brushes.White
            };

            var grid = new Grid();
            grid.Margin = new Thickness(20);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- Заголовок ---
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

            // --- Функция ограничения ввода только цифрами 1-9 ---
            void RestrictToDigits1To9(object sender, TextCompositionEventArgs e)
            {
                // Проверяем, является ли вводимый символ цифрой от 1 до 9
                foreach (char c in e.Text)
                {
                    if (!char.IsDigit(c) || c == '0')
                    {
                        e.Handled = true; // Блокируем ввод
                        return;
                    }
                }
            }

            // --- Функция ограничения ввода при вставке текста ---
            void RestrictPasteToDigits1To9(object sender, DataObjectPastingEventArgs e)
            {
                if (e.DataObject.GetDataPresent(typeof(string)))
                {
                    string pasteText = (string)e.DataObject.GetData(typeof(string));

                    // Проверяем все символы в вставляемом тексте
                    foreach (char c in pasteText)
                    {
                        if (!char.IsDigit(c) || c == '0')
                        {
                            e.CancelCommand(); // Отменяем вставку
                            return;
                        }
                    }
                }
                else
                {
                    e.CancelCommand(); // Отменяем вставку не текстовых данных
                }
            }

            // --- Обработчик нажатия клавиш для PasswordBox ---
            void PasswordBox_PreviewKeyDown(object sender, KeyEventArgs e)
            {
                // Блокируем пробел
                if (e.Key == Key.Space)
                {
                    e.Handled = true;
                    return;
                }

                // Разрешаем управляющие клавиши
                if (e.Key == Key.Back || e.Key == Key.Delete ||
                    e.Key == Key.Left || e.Key == Key.Right ||
                    e.Key == Key.Home || e.Key == Key.End ||
                    e.Key == Key.Tab || e.Key == Key.Enter)
                {
                    return;
                }

                // Проверяем цифровые клавиши
                if ((e.Key >= Key.D1 && e.Key <= Key.D9) ||
                    (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9))
                {
                    return; // Разрешаем цифры 1-9
                }

                // Блокируем ноль
                if (e.Key == Key.D0 || e.Key == Key.NumPad0)
                {
                    e.Handled = true;
                    return;
                }

                // Блокируем все остальные клавиши
                e.Handled = true;
            }

            // --- Поле "Старый пароль" ---
            var oldPasswordLabel = new Label
            {
                Content = "Старый пароль (только цифры 1-9)",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 5),
                Foreground = Brushes.White
            };

            var oldPasswordStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var oldPasswordBox = new PasswordBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                PasswordChar = '*'
            };

            // Подписываемся на события ограничения ввода для PasswordBox
            oldPasswordBox.PreviewKeyDown += PasswordBox_PreviewKeyDown;
            oldPasswordBox.PreviewTextInput += RestrictToDigits1To9;
            DataObject.AddPastingHandler(oldPasswordBox, RestrictPasteToDigits1To9);

            var oldPasswordPreviewTextBox = new TextBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Visibility = Visibility.Collapsed
            };

            // Подписываемся на события ограничения ввода для TextBox
            oldPasswordPreviewTextBox.PreviewTextInput += RestrictToDigits1To9;
            oldPasswordPreviewTextBox.PreviewKeyDown += PasswordBox_PreviewKeyDown;
            DataObject.AddPastingHandler(oldPasswordPreviewTextBox, RestrictPasteToDigits1To9);

            var showOldButton = new Button
            {
                Content = "👁️",
                Width = 30,
                Height = 25,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Gray,
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Margin = new Thickness(5, 0, 0, 0)
            };

            showOldButton.MouseEnter += (s, e) => showOldButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            showOldButton.MouseLeave += (s, e) => showOldButton.Background = Brushes.Transparent;

            showOldButton.PreviewMouseDown += (s, e) =>
            {
                oldPasswordPreviewTextBox.Text = oldPasswordBox.Password;
                oldPasswordBox.Visibility = Visibility.Collapsed;
                oldPasswordPreviewTextBox.Visibility = Visibility.Visible;
                oldPasswordPreviewTextBox.Focus();
                oldPasswordPreviewTextBox.CaretIndex = oldPasswordPreviewTextBox.Text.Length;
            };

            showOldButton.PreviewMouseUp += (s, e) =>
            {
                oldPasswordBox.Password = oldPasswordPreviewTextBox.Text;
                oldPasswordPreviewTextBox.Visibility = Visibility.Collapsed;
                oldPasswordBox.Visibility = Visibility.Visible;
                oldPasswordBox.Focus();
            };

            showOldButton.LostMouseCapture += (s, e) =>
            {
                oldPasswordBox.Password = oldPasswordPreviewTextBox.Text;
                oldPasswordPreviewTextBox.Visibility = Visibility.Collapsed;
                oldPasswordBox.Visibility = Visibility.Visible;
            };

            oldPasswordStack.Children.Add(oldPasswordBox);
            oldPasswordStack.Children.Add(oldPasswordPreviewTextBox);
            oldPasswordStack.Children.Add(showOldButton);

            var oldPasswordContainer = new StackPanel();
            oldPasswordContainer.Children.Add(oldPasswordLabel);
            oldPasswordContainer.Children.Add(oldPasswordStack);
            Grid.SetRow(oldPasswordContainer, 1);
            grid.Children.Add(oldPasswordContainer);

            // --- Поле "Новый пароль" ---
            var newPasswordLabel = new Label
            {
                Content = "Новый пароль (только цифры 1-9)",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 5),
                Foreground = Brushes.White
            };

            var newPasswordStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var newPasswordBox = new PasswordBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                PasswordChar = '*'
            };

            // Подписываемся на события ограничения ввода для PasswordBox
            newPasswordBox.PreviewKeyDown += PasswordBox_PreviewKeyDown;
            newPasswordBox.PreviewTextInput += RestrictToDigits1To9;
            DataObject.AddPastingHandler(newPasswordBox, RestrictPasteToDigits1To9);

            var newPasswordPreviewTextBox = new TextBox
            {
                Width = 250,
                Margin = new Thickness(0, 0, 0, 0),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                Visibility = Visibility.Collapsed
            };

            // Подписываемся на события ограничения ввода для TextBox
            newPasswordPreviewTextBox.PreviewTextInput += RestrictToDigits1To9;
            newPasswordPreviewTextBox.PreviewKeyDown += PasswordBox_PreviewKeyDown;
            DataObject.AddPastingHandler(newPasswordPreviewTextBox, RestrictPasteToDigits1To9);

            var showNewButton = new Button
            {
                Content = "👁️",
                Width = 30,
                Height = 25,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderBrush = Brushes.Gray,
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Margin = new Thickness(5, 0, 0, 0)
            };

            showNewButton.MouseEnter += (s, e) => showNewButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            showNewButton.MouseLeave += (s, e) => showNewButton.Background = Brushes.Transparent;

            showNewButton.PreviewMouseDown += (s, e) =>
            {
                newPasswordPreviewTextBox.Text = newPasswordBox.Password;
                newPasswordBox.Visibility = Visibility.Collapsed;
                newPasswordPreviewTextBox.Visibility = Visibility.Visible;
                newPasswordPreviewTextBox.Focus();
                newPasswordPreviewTextBox.CaretIndex = newPasswordPreviewTextBox.Text.Length;
            };

            showNewButton.PreviewMouseUp += (s, e) =>
            {
                newPasswordBox.Password = newPasswordPreviewTextBox.Text;
                newPasswordPreviewTextBox.Visibility = Visibility.Collapsed;
                newPasswordBox.Visibility = Visibility.Visible;
                newPasswordBox.Focus();
            };

            showNewButton.LostMouseCapture += (s, e) =>
            {
                newPasswordBox.Password = newPasswordPreviewTextBox.Text;
                newPasswordPreviewTextBox.Visibility = Visibility.Collapsed;
                newPasswordBox.Visibility = Visibility.Visible;
            };

            newPasswordStack.Children.Add(newPasswordBox);
            newPasswordStack.Children.Add(newPasswordPreviewTextBox);
            newPasswordStack.Children.Add(showNewButton);

            var newPasswordContainer = new StackPanel();
            newPasswordContainer.Children.Add(newPasswordLabel);
            newPasswordContainer.Children.Add(newPasswordStack);
            Grid.SetRow(newPasswordContainer, 2);
            grid.Children.Add(newPasswordContainer);

            // --- Кнопки ---
            var buttonStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            Style buttonStyle = new Style(typeof(Button));
            buttonStyle.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.White));
            buttonStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.Black));
            buttonStyle.Setters.Add(new Setter(Button.BorderBrushProperty, Brushes.Gray));
            buttonStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            buttonStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 6, 12, 6)));
            buttonStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
            buttonStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 80.0));

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

            // --- Обработчик кнопки "Сохранить" ---
            nextButton.Click += (s, e) =>
            {
                // Дополнительная валидация пароля
                bool IsValidPassword(string password)
                {
                    if (string.IsNullOrEmpty(password)) return false;

                    foreach (char c in password)
                    {
                        if (!char.IsDigit(c) || c == '0')
                        {
                            return false;
                        }
                    }
                    return true;
                }

                // Проверяем формат старого пароля
                if (!IsValidPassword(oldPasswordBox.Password))
                {
                    MessageBox.Show("Старый пароль должен содержать только цифры от 1 до 9.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Проверяем формат нового пароля
                if (string.IsNullOrEmpty(newPasswordBox.Password))
                {
                    MessageBox.Show("Новый пароль не может быть пустым.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!IsValidPassword(newPasswordBox.Password))
                {
                    MessageBox.Show("Новый пароль должен содержать только цифры от 1 до 9.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Проверяем, совпадает ли старый пароль
                if (oldPasswordBox.Password == Settings.CurrentPassword)
                {
                    Settings.CurrentPassword = newPasswordBox.Password;
                    MessageBox.Show("Пароль успешно изменён!", "Успех",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    dialog.Close();
                }
                else
                {
                    temp++;

                    MessageBox.Show("Старый пароль неверен!",
                        $"Количество попыток {3 - temp}",
                        MessageBoxButton.OK, MessageBoxImage.Warning);

                    if (temp == 3)
                    {
                        MessageBox.Show("Превышено количество попыток! Доступ заблокирован.",
                            "Блокировка", MessageBoxButton.OK, MessageBoxImage.Error);
                        dialog.Close();
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

        public void SaveSettings()
        {
            Settings.InvertX = InvertXCheckbox.IsChecked == true;
            Settings.InvertY = InvertYCheckbox.IsChecked == true;
            Settings.Sensitivity = SensitivityTextBox.Text;
            Settings.Brightness = BrightnessSlider.Value;
            Settings.IsGradient = Gradient.IsChecked == true;
            Settings.IsMonoColor = MonoColor.IsChecked == true;
            Settings.GradientSpeed = SpeedG.Value;
            Settings.ColorValue = ColorInputBox.Text;

            Settings.M1Value = M1.Text;
            Settings.M2Value = M2.Text;
            Settings.M3Value = M3.Text;
            Settings.M4Value = M4.Text;
            Settings.M5Value = M5.Text;
            Settings.M6Value = M6.Text;
            Settings.M7Value = M7.Text;
            Settings.M8Value = M8.Text;
            Settings.M9Value = M9.Text;

            if (Settings.IsMonoColor && !ValidateColorFormat(Settings.ColorValue))
            {
                MessageBox.Show("Недопустимый формат цвета. Используйте формат: xxx.yyy.zzz (где xxx, yyy, zzz - числа от 000 до 255).",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SendSettingsToArduino();
        }

        private void LoadAvailablePorts()
        {
            PortComboBox.Items.Clear();
            PortComboBox.Items.Add("не выбран");

            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                PortComboBox.Items.Add(port);
            }

            if (!string.IsNullOrEmpty(Settings.SelectedPortName))
            {
                if (Array.Exists(ports, p => p == Settings.SelectedPortName))
                {
                    PortComboBox.SelectedItem = Settings.SelectedPortName;
                }
                else
                {
                    Settings.SelectedPortName = "";
                    Settings.SerialPort = null;
                    PortComboBox.SelectedIndex = 0;
                }
            }
            else
            {
                PortComboBox.SelectedIndex = 0;
            }
        }
        
        //protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        //{
        //    if (Settings.SerialPort.IsOpen)
        //        Settings.SerialPort.Close();
        //    base.OnClosing(e);
        //}
    }




}