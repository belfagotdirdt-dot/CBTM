using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace mouseBoys
{
    public partial class MainWindow : Window
    {
        private const int DongleBaudRate = 9600;
        private const string DongleIdentifyResponse = "MOUSEBOYS_DONGLE";
        private const int ProbeTimeoutMs = 2500;
        private const int ProbeAttempts = 3;
        private const int LeonardoBootDelayMs = 1700;
        private const int AuthResponseTimeoutMs = 3500;

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
            public string SelectedPortName { get; set; }

            public static MouseSettings CreateDefault()
            {
                return new MouseSettings
                {
                    InvertX = false,
                    InvertY = false,
                    Sensitivity = "1",
                    Brightness = 50,
                    IsGradient = true,
                    IsMonoColor = false,
                    ColorValue = "000.000.000",
                    GradientSpeed = 50,
                    M1Value = "left click",
                    M2Value = "right click",
                    M3Value = "middle click",
                    M4Value = "back",
                    M5Value = "forward",
                    M6Value = "Volume Up",
                    M7Value = "Volume Down",
                    M8Value = "Mute",
                    M9Value = "Task View",
                    SelectedPortName = string.Empty
                };
            }
        }

        private MouseSettings _settings = MouseSettings.CreateDefault();
        private SerialPort? _serialPort;
        private readonly StringBuilder _serialLineBuffer = new();
        private DispatcherTimer? _portRefreshTimer;
        private bool _isConnected;
        private bool _isConnecting;
        private bool _isUpdatingPortCombo;
        private bool _isWaitingForSaveResponse;

        private bool _isAuthenticated;
        private Task<bool>? _authFlowTask;
        private readonly object _awaitResponseLock = new();
        private TaskCompletionSource<string>? _awaitResponseTcs;
        private Func<string, bool>? _awaitResponseMatcher;

        public MainWindow()
        {
            InitializeComponent();
            SetupEventHandlers();
            ApplySettingsToUI();
            UpdateAuthUiState(false);
            RefreshComPorts();
            StartPortRefreshTimer();
            _ = AutoConnectToDongleAsync();
        }

        #region Init

        private void SetupEventHandlers()
        {
            SaveButton.Click += SaveButton_Click;
            ResetButton.Click += ResetButton_Click;
            ChangePasswordButton.Click += ChangePasswordButton_Click;

            PortComboBox.SelectionChanged += PortComboBox_SelectionChanged;

            ColorInputBox.PreviewTextInput += ColorInputBox_PreviewTextInput;
            ColorInputBox.LostFocus += ColorInputBox_LostFocus;
            SensitivityTextBox.PreviewTextInput += SensitivityTextBox_PreviewTextInput;
        }

        private void StartPortRefreshTimer()
        {
            _portRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _portRefreshTimer.Tick += async (_, _) => await OnPortRefreshTickAsync();
            _portRefreshTimer.Start();
        }

        #endregion

        #region COM

        private static SerialPort CreateSerialPort(string portName)
        {
            return new SerialPort
            {
                PortName = portName,
                BaudRate = DongleBaudRate,
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                ReadTimeout = 500,
                WriteTimeout = 1000,
                NewLine = "\n",
                Encoding = Encoding.UTF8,
                DtrEnable = true,
                RtsEnable = true,
                Handshake = Handshake.None
            };
        }

        private void RefreshComPorts()
        {
            string? selected = PortComboBox.SelectedItem?.ToString();
            string[] ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();

            bool changed = ports.Length != PortComboBox.Items.Count;
            if (!changed)
            {
                for (int i = 0; i < ports.Length; i++)
                {
                    if (!string.Equals(PortComboBox.Items[i]?.ToString(), ports[i], StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return;

            _isUpdatingPortCombo = true;
            PortComboBox.Items.Clear();
            foreach (string p in ports)
            {
                PortComboBox.Items.Add(p);
            }

            if (!string.IsNullOrWhiteSpace(_settings.SelectedPortName) && ports.Contains(_settings.SelectedPortName))
            {
                PortComboBox.SelectedItem = _settings.SelectedPortName;
            }
            else if (!string.IsNullOrWhiteSpace(selected) && ports.Contains(selected))
            {
                PortComboBox.SelectedItem = selected;
            }
            else if (ports.Length > 0)
            {
                PortComboBox.SelectedIndex = 0;
            }
            _isUpdatingPortCombo = false;

            Log($"Порты: {(ports.Length == 0 ? "не найдены" : string.Join(", ", ports))}");
        }

        private async Task OnPortRefreshTickAsync()
        {
            RefreshComPorts();

            if (_isConnected && _serialPort != null)
            {
                if (!SerialPort.GetPortNames().Contains(_serialPort.PortName))
                {
                    Log($"Порт {_serialPort.PortName} отключен", "WARNING");
                    DisconnectFromPort();
                }
            }

            if (!_isConnected)
            {
                await AutoConnectToDongleAsync();
            }
        }

        private async Task<bool> AutoConnectToDongleAsync()
        {
            if (_isConnected || _isConnecting)
            {
                return _isConnected;
            }

            _isConnecting = true;
            try
            {
                foreach (string port in GetCandidatePorts())
                {
                    if (await IsDonglePortAsync(port))
                    {
                        return ConnectToPort(port);
                    }
                }

                UpdateStatusBar("Донгл не найден");
                return false;
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private IEnumerable<string> GetCandidatePorts()
        {
            string[] ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            var ordered = new List<string>();

            if (!string.IsNullOrWhiteSpace(_settings.SelectedPortName) && ports.Contains(_settings.SelectedPortName))
            {
                ordered.Add(_settings.SelectedPortName);
            }

            foreach (string port in ports)
            {
                if (!ordered.Contains(port))
                {
                    ordered.Add(port);
                }
            }

            return ordered;
        }

        private static async Task<bool> IsDonglePortAsync(string portName)
        {
            return await Task.Run(() =>
            {
                for (int attempt = 1; attempt <= ProbeAttempts; attempt++)
                {
                    try
                    {
                        using SerialPort probe = CreateSerialPort(portName);
                        probe.Open();

                        // Leonardo обычно делает reset после открытия порта.
                        Thread.Sleep(LeonardoBootDelayMs);

                        probe.DiscardInBuffer();
                        probe.DiscardOutBuffer();

                        DateTime endAt = DateTime.UtcNow.AddMilliseconds(ProbeTimeoutMs);
                        while (DateTime.UtcNow < endAt)
                        {
                            probe.WriteLine("IDENTIFY");

                            DateTime readUntil = DateTime.UtcNow.AddMilliseconds(450);
                            while (DateTime.UtcNow < readUntil)
                            {
                                try
                                {
                                    string line = probe.ReadLine().Trim();
                                    if (line.Equals(DongleIdentifyResponse, StringComparison.OrdinalIgnoreCase))
                                    {
                                        return true;
                                    }
                                }
                                catch (TimeoutException)
                                {
                                    break;
                                }
                            }

                            Thread.Sleep(120);
                        }
                    }
                    catch
                    {
                        // Порт может быть временно занят/перезапускаться.
                    }

                    Thread.Sleep(220);
                }

                return false;
            });
        }

        private bool ConnectToPort(string portName)
        {
            DisconnectFromPort();

            try
            {
                _serialPort = CreateSerialPort(portName);
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                _isConnected = true;
                _settings.SelectedPortName = portName;
                _isAuthenticated = false;
                UpdateAuthUiState(false);

                _isUpdatingPortCombo = true;
                PortComboBox.SelectedItem = portName;
                _isUpdatingPortCombo = false;

                lock (_serialLineBuffer)
                {
                    _serialLineBuffer.Clear();
                }

                Log($"Подключено к {portName}", "SUCCESS");
                UpdateStatusBar($"Подключено к {portName}, требуется пароль");

                _ = EnsureAuthenticatedAsync(showUi: true);
                return true;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _isAuthenticated = false;
                UpdateAuthUiState(false);
                Log($"Ошибка подключения к {portName}: {ex.Message}", "ERROR");
                UpdateStatusBar("Ошибка подключения");
                return false;
            }
        }

        private void DisconnectFromPort()
        {
            try
            {
                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    if (_serialPort.IsOpen)
                    {
                        string previousPort = _serialPort.PortName;
                        _serialPort.Close();
                        Log($"Отключено от {previousPort}");
                    }

                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка отключения: {ex.Message}", "ERROR");
            }
            finally
            {
                _isConnected = false;
                _isAuthenticated = false;
                _isWaitingForSaveResponse = false;
                _authFlowTask = null;
                lock (_awaitResponseLock)
                {
                    _awaitResponseMatcher = null;
                    _awaitResponseTcs?.TrySetCanceled();
                    _awaitResponseTcs = null;
                }
                UpdateAuthUiState(false);
                UpdateStatusBar("Отключено");
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    return;
                }

                string incoming = _serialPort.ReadExisting();
                if (string.IsNullOrEmpty(incoming))
                {
                    return;
                }

                List<string> completeLines = new();
                lock (_serialLineBuffer)
                {
                    _serialLineBuffer.Append(incoming);
                    string buffered = _serialLineBuffer.ToString();
                    string[] split = buffered.Split('\n');

                    for (int i = 0; i < split.Length - 1; i++)
                    {
                        string line = split[i].Trim('\r', ' ', '\t');
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            completeLines.Add(line);
                        }
                    }

                    _serialLineBuffer.Clear();
                    _serialLineBuffer.Append(split[^1]);
                }

                foreach (string line in completeLines)
                {
                    Dispatcher.Invoke(() => ProcessReceivedData(line));
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка приема данных: {ex.Message}", "ERROR");
            }
        }

        private void ProcessReceivedData(string data)
        {
            Log($"<- {data}", "RX");
            TryCompleteAwaitedResponse(data);

            if (data.StartsWith("SERIAL_IN:", StringComparison.Ordinal) ||
                data.StartsWith("RADIO:", StringComparison.Ordinal))
            {
                // Это диагностический трафик: уже показан в логе RX, дополнительная обработка не нужна.
                return;
            }

            if (data == DongleIdentifyResponse)
            {
                UpdateStatusBar($"Подключено к {_settings.SelectedPortName}");
                return;
            }

            if (data == "AUTH_REQUIRED")
            {
                _isAuthenticated = false;
                UpdateAuthUiState(false);
                UpdateStatusBar("Требуется ввод пароля");
                return;
            }

            if (data == "NO_PASSWORD")
            {
                _isAuthenticated = false;
                UpdateAuthUiState(false);
                UpdateStatusBar("Пароль не установлен");
                return;
            }

            if (data == "AUTH_OK")
            {
                _isAuthenticated = true;
                UpdateAuthUiState(true);
                UpdateStatusBar("Авторизация успешна");
                return;
            }

            if (data == "AUTH_FAILED")
            {
                _isAuthenticated = false;
                UpdateAuthUiState(false);
                UpdateStatusBar("Неверная комбинация");
                return;
            }

            if (data == "PASSWORD_CHANGED")
            {
                _isAuthenticated = true;
                UpdateAuthUiState(true);
                UpdateStatusBar("Пароль обновлен");
                return;
            }

            if (data.StartsWith("SETTINGS:", StringComparison.Ordinal))
            {
                if (!_isAuthenticated)
                {
                    Log("SETTINGS проигнорированы до авторизации", "WARNING");
                    return;
                }

                if (TryParseSettings(data.Substring(9), out MouseSettings parsed))
                {
                    parsed.SelectedPortName = _settings.SelectedPortName;
                    _settings = parsed;
                    ApplySettingsToUI();
                    UpdateStatusBar("Настройки загружены с донгла");
                    Log("Настройки загружены", "SUCCESS");
                }
                else
                {
                    Log("Некорректный формат SETTINGS", "WARNING");
                    UpdateStatusBar("Ошибка чтения настроек");
                }

                return;
            }

            if (data == "OK")
            {
                _isWaitingForSaveResponse = false;
                UpdateStatusBar("Настройки отправлены на донгл");
                Log("Настройки применены донглом", "SUCCESS");
                return;
            }

            if (data.StartsWith("ERROR", StringComparison.Ordinal))
            {
                _isWaitingForSaveResponse = false;
                UpdateStatusBar("Ошибка от донгла");
                Log($"Донгл вернул ошибку: {data}", "ERROR");
                MessageBox.Show($"Донгл вернул ошибку: {data}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (data == "PONG")
            {
                return;
            }

            Log($"Необработанный ответ: {data}", "INFO");
        }

        private void SendCommand(string command, bool showErrors = true)
        {
            if (_serialPort == null || !_serialPort.IsOpen || !_isConnected)
            {
                if (showErrors)
                {
                    MessageBox.Show("Донгл не подключен", "Нет соединения", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return;
            }

            try
            {
                _serialPort.WriteLine(command);
                Log($"-> {command}", "TX");
            }
            catch (Exception ex)
            {
                Log($"Ошибка отправки: {ex.Message}", "ERROR");
                if (showErrors)
                {
                    MessageBox.Show($"Ошибка отправки команды:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void PortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingPortCombo)
            {
                return;
            }

            string? selected = PortComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(selected))
            {
                return;
            }

            if (_isConnected && _serialPort?.PortName == selected)
            {
                return;
            }

            bool isDongle = await IsDonglePortAsync(selected);
            if (!isDongle)
            {
                // Повторная проверка снижает ложные срабатывания после hot-plug/reset.
                await Task.Delay(300);
                isDongle = await IsDonglePortAsync(selected);
            }

            if (isDongle)
            {
                ConnectToPort(selected);
            }
            else
            {
                Log($"{selected} не ответил как донгл (проверь прошивку и скорость 9600)", "WARNING");
            }
        }

        private async Task<bool> EnsureDongleConnectionAsync()
        {
            if (_isConnected && _serialPort != null && _serialPort.IsOpen)
            {
                return true;
            }

            string? selected = PortComboBox.SelectedItem?.ToString();
            if (!string.IsNullOrWhiteSpace(selected) && await IsDonglePortAsync(selected))
            {
                return ConnectToPort(selected);
            }

            if (await AutoConnectToDongleAsync())
            {
                return true;
            }

            MessageBox.Show(
                "Не удалось найти донгл. Подключите Arduino Leonardo и попробуйте снова.",
                "Донгл не найден",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private void UpdateAuthUiState(bool authenticated)
        {
            _isAuthenticated = authenticated;
            bool enabled = authenticated;

            if (SaveButton != null) SaveButton.IsEnabled = enabled;
            if (ResetButton != null) ResetButton.IsEnabled = enabled;

            if (InvertXCheckbox != null) InvertXCheckbox.IsEnabled = enabled;
            if (InvertYCheckbox != null) InvertYCheckbox.IsEnabled = enabled;
            if (SensitivityTextBox != null) SensitivityTextBox.IsEnabled = enabled;
            if (BrightnessSlider != null) BrightnessSlider.IsEnabled = enabled;
            if (Gradient != null) Gradient.IsEnabled = enabled;
            if (MonoColor != null) MonoColor.IsEnabled = enabled;
            if (ColorInputBox != null) ColorInputBox.IsEnabled = enabled;
            if (SpeedG != null) SpeedG.IsEnabled = enabled;
            if (M1 != null) M1.IsEnabled = enabled;
            if (M2 != null) M2.IsEnabled = enabled;
            if (M3 != null) M3.IsEnabled = enabled;
            if (M4 != null) M4.IsEnabled = enabled;
            if (M5 != null) M5.IsEnabled = enabled;
            if (M6 != null) M6.IsEnabled = enabled;
            if (M7 != null) M7.IsEnabled = enabled;
            if (M8 != null) M8.IsEnabled = enabled;
            if (M9 != null) M9.IsEnabled = enabled;
        }

        private static bool IsValidPasswordCombo(string? combo)
        {
            if (string.IsNullOrWhiteSpace(combo))
            {
                return false;
            }

            return Regex.IsMatch(combo, "^[123]{1,16}$");
        }

        private async Task<string?> SendCommandAndAwaitAsync(string command, Func<string, bool> matcher, int timeoutMs = AuthResponseTimeoutMs)
        {
            if (_serialPort == null || !_serialPort.IsOpen || !_isConnected)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_awaitResponseLock)
            {
                _awaitResponseMatcher = matcher;
                _awaitResponseTcs = tcs;
            }

            SendCommand(command);

            Task done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done == tcs.Task)
            {
                return await tcs.Task;
            }

            lock (_awaitResponseLock)
            {
                if (ReferenceEquals(_awaitResponseTcs, tcs))
                {
                    _awaitResponseMatcher = null;
                    _awaitResponseTcs = null;
                }
            }

            return null;
        }

        private void TryCompleteAwaitedResponse(string data)
        {
            TaskCompletionSource<string>? tcs = null;
            lock (_awaitResponseLock)
            {
                if (_awaitResponseMatcher != null && _awaitResponseMatcher(data))
                {
                    tcs = _awaitResponseTcs;
                    _awaitResponseMatcher = null;
                    _awaitResponseTcs = null;
                }
            }

            tcs?.TrySetResult(data);
        }

        private async Task<bool> EnsureAuthenticatedAsync(bool showUi)
        {
            if (!_isConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            if (_isAuthenticated)
            {
                return true;
            }

            if (_authFlowTask != null)
            {
                return await _authFlowTask;
            }

            _authFlowTask = EnsureAuthenticatedCoreAsync(showUi);
            try
            {
                return await _authFlowTask;
            }
            finally
            {
                _authFlowTask = null;
            }
        }

        private async Task<bool> EnsureAuthenticatedCoreAsync(bool showUi)
        {
            UpdateAuthUiState(false);
            UpdateStatusBar("Проверка авторизации...");

            string? state = await SendCommandAndAwaitAsync(
                "GET_AUTH_STATE",
                s => s == "AUTH_REQUIRED" || s == "NO_PASSWORD" || s == "AUTH_OK");

            if (string.IsNullOrWhiteSpace(state))
            {
                UpdateStatusBar("Донгл не ответил на проверку пароля");
                if (showUi)
                {
                    MessageBox.Show("Донгл не ответил на запрос авторизации.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            if (state == "AUTH_OK")
            {
                UpdateAuthUiState(true);
                SendCommand("GET_SETTINGS", showErrors: false);
                return true;
            }

            if (!showUi)
            {
                return false;
            }

            if (state == "NO_PASSWORD")
            {
                while (_isConnected)
                {
                    PasswordComboDialog createDialog = new PasswordComboDialog(
                        "Установка пароля",
                        "На донгле нет пароля.\nВведите комбинацию (только цифры 1/2/3):",
                        "Установить");

                    if (createDialog.ShowDialog() != true)
                    {
                        return false;
                    }

                    string combo = createDialog.PasswordCombo;
                    if (!IsValidPasswordCombo(combo))
                    {
                        MessageBox.Show("Неверный формат. Используйте только цифры 1, 2, 3.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    string? resp = await SendCommandAndAwaitAsync(
                        $"SET_PASSWORD:{combo}",
                        s => s == "PASSWORD_CHANGED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                    if (resp == "PASSWORD_CHANGED")
                    {
                        UpdateAuthUiState(true);
                        SendCommand("GET_SETTINGS", showErrors: false);
                        MessageBox.Show("Пароль установлен.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                        return true;
                    }

                    MessageBox.Show($"Не удалось установить пароль: {resp ?? "таймаут"}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return false;
            }

            while (_isConnected)
            {
                PasswordComboDialog authDialog = new PasswordComboDialog(
                    "Авторизация",
                    "Введите пароль-комбинацию кнопок (цифры 1/2/3):",
                    "Войти");

                if (authDialog.ShowDialog() != true)
                {
                    return false;
                }

                string combo = authDialog.PasswordCombo;
                if (!IsValidPasswordCombo(combo))
                {
                    MessageBox.Show("Неверный формат. Используйте только цифры 1, 2, 3.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                string? authResp = await SendCommandAndAwaitAsync(
                    $"AUTH:{combo}",
                    s => s == "AUTH_OK" || s == "AUTH_FAILED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                if (authResp == "AUTH_OK")
                {
                    UpdateAuthUiState(true);
                    SendCommand("GET_SETTINGS", showErrors: false);
                    return true;
                }

                MessageBox.Show("Неверная комбинация. Попробуйте снова.", "Авторизация", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }

        #endregion

        #region Settings serialization

        private void CollectSettingsFromUI()
        {
            _settings.InvertX = InvertXCheckbox.IsChecked ?? false;
            _settings.InvertY = InvertYCheckbox.IsChecked ?? false;
            _settings.Sensitivity = string.IsNullOrWhiteSpace(SensitivityTextBox.Text) ? "1" : SensitivityTextBox.Text.Trim();
            _settings.Brightness = BrightnessSlider.Value;
            _settings.IsGradient = Gradient.IsChecked ?? true;
            _settings.IsMonoColor = MonoColor.IsChecked ?? false;
            _settings.ColorValue = NormalizeColor(ColorInputBox.Text);
            _settings.GradientSpeed = SpeedG.Value;
            _settings.M1Value = SanitizeButtonText(M1.Text);
            _settings.M2Value = SanitizeButtonText(M2.Text);
            _settings.M3Value = SanitizeButtonText(M3.Text);
            _settings.M4Value = SanitizeButtonText(M4.Text);
            _settings.M5Value = SanitizeButtonText(M5.Text);
            _settings.M6Value = SanitizeButtonText(M6.Text);
            _settings.M7Value = SanitizeButtonText(M7.Text);
            _settings.M8Value = SanitizeButtonText(M8.Text);
            _settings.M9Value = SanitizeButtonText(M9.Text);
        }

        private void ApplySettingsToUI()
        {
            InvertXCheckbox.IsChecked = _settings.InvertX;
            InvertYCheckbox.IsChecked = _settings.InvertY;
            SensitivityTextBox.Text = _settings.Sensitivity;
            BrightnessSlider.Value = _settings.Brightness;
            Gradient.IsChecked = _settings.IsGradient;
            MonoColor.IsChecked = _settings.IsMonoColor;
            ColorInputBox.Text = _settings.ColorValue;
            SpeedG.Value = _settings.GradientSpeed;
            M1.Text = _settings.M1Value;
            M2.Text = _settings.M2Value;
            M3.Text = _settings.M3Value;
            M4.Text = _settings.M4Value;
            M5.Text = _settings.M5Value;
            M6.Text = _settings.M6Value;
            M7.Text = _settings.M7Value;
            M8.Text = _settings.M8Value;
            M9.Text = _settings.M9Value;
        }

        private string BuildSettingsPayload()
        {
            string buttonPayload = string.Join("|", new[]
            {
                _settings.M1Value, _settings.M2Value, _settings.M3Value, _settings.M4Value, _settings.M5Value,
                _settings.M6Value, _settings.M7Value, _settings.M8Value, _settings.M9Value
            });

            return string.Join(",", new[]
            {
                _settings.InvertX ? "1" : "0",
                _settings.InvertY ? "1" : "0",
                _settings.Sensitivity,
                ((int)_settings.Brightness).ToString(),
                _settings.IsGradient ? "1" : "0",
                _settings.ColorValue,
                ((int)_settings.GradientSpeed).ToString(),
                buttonPayload
            });
        }

        private bool TryParseSettings(string payload, out MouseSettings parsed)
        {
            parsed = MouseSettings.CreateDefault();

            int[] commas = payload
                .Select((ch, idx) => new { ch, idx })
                .Where(x => x.ch == ',')
                .Take(7)
                .Select(x => x.idx)
                .ToArray();

            if (commas.Length < 7)
            {
                return false;
            }

            string[] fields = new string[8];
            int start = 0;
            for (int i = 0; i < 7; i++)
            {
                fields[i] = payload.Substring(start, commas[i] - start).Trim();
                start = commas[i] + 1;
            }
            fields[7] = payload.Substring(start).Trim();

            parsed.InvertX = fields[0] == "1";
            parsed.InvertY = fields[1] == "1";
            parsed.Sensitivity = string.IsNullOrWhiteSpace(fields[2]) ? "1" : fields[2];
            parsed.Brightness = int.TryParse(fields[3], out int brightness) ? Clamp(brightness, 0, 100) : 50;
            parsed.IsGradient = fields[4] == "1";
            parsed.IsMonoColor = !parsed.IsGradient;
            parsed.ColorValue = NormalizeColor(fields[5]);
            parsed.GradientSpeed = int.TryParse(fields[6], out int speed) ? Clamp(speed, 0, 100) : 50;

            string[] buttons = fields[7].Split('|');
            if (buttons.Length > 0) parsed.M1Value = buttons[0];
            if (buttons.Length > 1) parsed.M2Value = buttons[1];
            if (buttons.Length > 2) parsed.M3Value = buttons[2];
            if (buttons.Length > 3) parsed.M4Value = buttons[3];
            if (buttons.Length > 4) parsed.M5Value = buttons[4];
            if (buttons.Length > 5) parsed.M6Value = buttons[5];
            if (buttons.Length > 6) parsed.M7Value = buttons[6];
            if (buttons.Length > 7) parsed.M8Value = buttons[7];
            if (buttons.Length > 8) parsed.M9Value = buttons[8];

            return true;
        }

        private static string SanitizeButtonText(string value)
        {
            string sanitized = (value ?? string.Empty).Trim();
            sanitized = sanitized.Replace(',', ' ');
            sanitized = sanitized.Replace('|', '/');
            return string.IsNullOrWhiteSpace(sanitized) ? "none" : sanitized;
        }

        private static string NormalizeColor(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "000.000.000";
            }

            string[] parts = text.Split('.');
            if (parts.Length != 3)
            {
                return "000.000.000";
            }

            int[] rgb = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i], out int value))
                {
                    value = 0;
                }

                rgb[i] = Clamp(value, 0, 255);
            }

            return $"{rgb[0]:000}.{rgb[1]:000}.{rgb[2]:000}";
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        #endregion

        #region UI events

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureDongleConnectionAsync())
            {
                return;
            }

            if (!await EnsureAuthenticatedAsync(showUi: true))
            {
                return;
            }

            CollectSettingsFromUI();
            _isWaitingForSaveResponse = true;
            UpdateStatusBar("Отправка настроек на донгл...");
            SendCommand($"SET_SETTINGS:{BuildSettingsPayload()}");
        }

        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureDongleConnectionAsync())
            {
                return;
            }

            if (!await EnsureAuthenticatedAsync(showUi: true))
            {
                return;
            }

            UpdateStatusBar("Запрос настроек с донгла...");
            SendCommand("GET_SETTINGS");
        }

        private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureDongleConnectionAsync())
            {
                return;
            }

            if (!await EnsureAuthenticatedAsync(showUi: true))
            {
                return;
            }

            while (_isConnected)
            {
                PasswordComboDialog dialog = new PasswordComboDialog(
                    "Смена пароля",
                    "Введите новую комбинацию (только цифры 1/2/3):",
                    "Сохранить");

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                string combo = dialog.PasswordCombo;
                if (!IsValidPasswordCombo(combo))
                {
                    MessageBox.Show("Неверный формат. Используйте только цифры 1, 2, 3.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                string? resp = await SendCommandAndAwaitAsync(
                    $"SET_PASSWORD:{combo}",
                    s => s == "PASSWORD_CHANGED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                if (resp == "PASSWORD_CHANGED")
                {
                    MessageBox.Show("Пароль успешно изменен.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                MessageBox.Show($"Ошибка смены пароля: {resp ?? "таймаут"}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ColorInputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9.]");
        }

        private void ColorInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ColorInputBox.Text = NormalizeColor(ColorInputBox.Text);
        }

        private void SensitivityTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = Regex.IsMatch(e.Text, "[^0-9.]");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _portRefreshTimer?.Stop();
            DisconnectFromPort();
        }

        #endregion

        #region Diagnostics

        private void Log(string message, string level = "INFO")
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            Debug.WriteLine(line);

            Dispatcher.Invoke(() =>
            {
                if (LogTextBox != null)
                {
                    LogTextBox.AppendText(line + Environment.NewLine);
                    LogTextBox.ScrollToEnd();
                }
            });
        }

        private void UpdateStatusBar(string message)
        {
            Title = $"CRM - Управление мышью | {message}";
        }

        #endregion
    }

    public class PasswordComboDialog : Window
    {
        private readonly TextBox _comboBox;
        public string PasswordCombo { get; private set; } = string.Empty;

        public PasswordComboDialog(string title, string message, string okButtonText)
        {
            Title = title;
            Width = 390;
            Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Grid root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(text, 0);

            _comboBox = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 16,
                MaxLength = 16,
                TextAlignment = TextAlignment.Center
            };
            _comboBox.PreviewTextInput += (_, e) => e.Handled = !Regex.IsMatch(e.Text, "^[123]+$");
            _comboBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Submit();
                }
            };
            Grid.SetRow(_comboBox, 1);

            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button ok = new Button { Content = okButtonText, Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => Submit();
            Button cancel = new Button { Content = "Отмена", Width = 90 };
            cancel.Click += (_, _) => DialogResult = false;

            panel.Children.Add(ok);
            panel.Children.Add(cancel);
            Grid.SetRow(panel, 2);

            root.Children.Add(text);
            root.Children.Add(_comboBox);
            root.Children.Add(panel);

            Content = root;
            Loaded += (_, _) => _comboBox.Focus();
        }

        private void Submit()
        {
            string value = (_comboBox.Text ?? string.Empty).Trim();
            if (!Regex.IsMatch(value, "^[123]{1,16}$"))
            {
                MessageBox.Show("Комбинация должна содержать только цифры 1, 2, 3.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PasswordCombo = value;
            DialogResult = true;
        }
    }
}
