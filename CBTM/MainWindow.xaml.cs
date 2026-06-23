using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CBTM
{
    public enum HostActionType
    {
        None = 0,
        Program,
        Text,
        Url
    }

    public class HostActionConfig
    {
        public HostActionType Type { get; set; } = HostActionType.None;
        public string Payload { get; set; } = string.Empty;
    }


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
                    SelectedPortName = string.Empty
                };
            }
        }

        private static readonly JsonSerializerOptions HostJsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // Действия ПК для кнопок M1..M5. После доработки прошивки физические
        // нажатия приходят со всех пяти кнопок (M4=D5, M5=A4 на плате мыши).
        private const int PhysicalButtonCount = 5;
        private readonly HostActionConfig[] _hostActions = CreateEmptyHostActions();

        private MouseSettings _settings = MouseSettings.CreateDefault();
        private SerialPort? _serialPort;
        private readonly StringBuilder _serialLineBuffer = new();
        private DispatcherTimer? _portRefreshTimer;
        private bool _isConnected;
        private bool _isConnecting;
        private bool _isUpdatingPortCombo;

        private bool _isAuthenticated;
        private Task<bool>? _authFlowTask;
        private readonly object _awaitResponseLock = new();
        private TaskCompletionSource<string>? _awaitResponseTcs;
        private Func<string, bool>? _awaitResponseMatcher;

        private const int MaxPasswordChangeAttempts = 3;
        private const int PasswordChangeLockMinutes = 1;
        private int _failedVerificationAttempts = 0;
        private DateTime? _verificationLockUntil = null;
        private DispatcherTimer? _lockUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();
            LoadHostActions();
            SetupEventHandlers();
            ApplySettingsToUI();
            UpdateAuthUiState(false);
            RefreshComPorts();
            StartPortRefreshTimer();
            _ = AutoConnectToDongleAsync();

            // Таймер работает только во время блокировки смены пароля — иначе он бы
            // каждую секунду затирал hover-подсветку кнопки. Запускается в момент
            // блокировки и останавливается, когда она истекает.
            _lockUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _lockUpdateTimer.Tick += (_, _) => UpdatePasswordChangeButtonState();
            UpdatePasswordChangeButtonState();
        }

        #region Init

        private void SetupEventHandlers()
        {
            SaveButton.Click += SaveButton_Click;
            ResetButton.Click += ResetButton_Click;
            ChangePasswordButton.Click += ChangePasswordButton_Click;
            HelpButton.Click += HelpButton_Click;

            M1HostButton.Click += (_, _) => ConfigureHostAction(0, "M1");
            M2HostButton.Click += (_, _) => ConfigureHostAction(1, "M2");
            M3HostButton.Click += (_, _) => ConfigureHostAction(2, "M3");
            M4HostButton.Click += (_, _) => ConfigureHostAction(3, "M4");
            M5HostButton.Click += (_, _) => ConfigureHostAction(4, "M5");

            PortComboBox.SelectionChanged += PortComboBox_SelectionChanged;

            ColorInputBox.PreviewTextInput += ColorInputBox_PreviewTextInput;
            ColorInputBox.LostFocus += ColorInputBox_LostFocus;
            SensitivityTextBox.PreviewTextInput += SensitivityTextBox_PreviewTextInput;

            ChangePasswordButton.MouseEnter += ChangePasswordButton_MouseEnter;
            ChangePasswordButton.MouseLeave += ChangePasswordButton_MouseLeave;

            // Обработчики переключения режимов подсветки
            MonoColor.Checked += MonoColor_Checked;
            MonoColor.Unchecked += MonoColor_Unchecked;
            Gradient.Checked += Gradient_Checked;
            Gradient.Unchecked += Gradient_Unchecked;
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

            if (data.StartsWith("BTN:", StringComparison.Ordinal))
            {
                if (int.TryParse(data.Substring(4).Trim(), out int buttonIndex))
                {
                    ExecuteHostAction(buttonIndex);
                }

                return;
            }

            if (data.StartsWith("SERIAL_IN:", StringComparison.Ordinal) ||
                data.StartsWith("RADIO:", StringComparison.Ordinal))
            {
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
                UpdateStatusBar("Настройки отправлены на донгл");
                Log("Настройки применены донглом", "SUCCESS");
                return;
            }

            if (data.StartsWith("ERROR", StringComparison.Ordinal))
            {
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
                await Task.Delay(300);
                isDongle = await IsDonglePortAsync(selected);
            }

            if (isDongle)
            {
                ConnectToPort(selected);
            }
            else
            {
                Log($"{selected} не ответил как манипулятор", "WARNING");
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
                "Не удалось найти донгл. Подключите манипулятор и попробуйте снова.",
                "Донгл не найден",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private void UpdateAuthUiState(bool authenticated)
        {
            _isAuthenticated = authenticated;
            bool enabled = authenticated;

            // Блокируем/разблокируем сами Expander'ы
            if (CursorExpander != null) CursorExpander.IsEnabled = enabled;
            if (LightingExpander != null) LightingExpander.IsEnabled = enabled;
            if (ButtonsExpander != null) ButtonsExpander.IsEnabled = enabled;

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

            RefreshHostUi();
        }

        private static bool IsValidPasswordCombo(string? combo)
        {
            if (string.IsNullOrWhiteSpace(combo))
            {
                return false;
            }

            return Regex.IsMatch(combo, "^[0-9]{1,16}$");
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

        private async Task RequestSettingsFromDeviceAsync()
        {
            UpdateStatusBar("Загрузка настроек с донгла...");
            string? resp = await SendCommandAndAwaitAsync(
                "GET_SETTINGS",
                s => s.StartsWith("SETTINGS:", StringComparison.Ordinal) || s.StartsWith("ERROR", StringComparison.Ordinal),
                timeoutMs: 3000);

            if (resp == null)
            {
                Log("Настройки не получены с донгла (таймаут)", "WARNING");
                UpdateStatusBar("Не удалось загрузить настройки");
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
                _ = RequestSettingsFromDeviceAsync();
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
                        "На донгле нет пароля.\nВведите комбинацию (только цифры):",
                        "Установить");

                    if (createDialog.ShowDialog() != true)
                    {
                        return false;
                    }

                    string combo = createDialog.PasswordCombo;
                    if (!IsValidPasswordCombo(combo))
                    {
                        MessageBox.Show("Неверный формат. Используйте только цифры.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    string? resp = await SendCommandAndAwaitAsync(
                        $"SET_PASSWORD:{combo}",
                        s => s == "PASSWORD_CHANGED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                    if (resp == "PASSWORD_CHANGED")
                    {
                        UpdateAuthUiState(true);
                        _ = RequestSettingsFromDeviceAsync();
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
                    "Введите пароль-комбинацию цифры:",
                    "Войти");

                if (authDialog.ShowDialog() != true)
                {
                    return false;
                }

                string combo = authDialog.PasswordCombo;
                if (!IsValidPasswordCombo(combo))
                {
                    MessageBox.Show("Неверный формат. Используйте только цифры", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                string? authResp = await SendCommandAndAwaitAsync(
                    $"AUTH:{combo}",
                    s => s == "AUTH_OK" || s == "AUTH_FAILED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                if (authResp == "AUTH_OK")
                {
                    UpdateAuthUiState(true);
                    _ = RequestSettingsFromDeviceAsync();
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
            _settings.M1Value = ResolveButtonToken(0, M1.Text);
            _settings.M2Value = ResolveButtonToken(1, M2.Text);
            _settings.M3Value = ResolveButtonToken(2, M3.Text);
            _settings.M4Value = ResolveButtonToken(3, M4.Text);
            _settings.M5Value = ResolveButtonToken(4, M5.Text);
        }

        // Если на кнопку назначено действие ПК, на донгл уходит маркер "host"
        // (донгл не делает HID-клик, а присылает "BTN:n" при нажатии).
        private string ResolveButtonToken(int index, string text)
        {
            if (index >= 0 && index < _hostActions.Length && _hostActions[index].Type != HostActionType.None)
            {
                return "host";
            }

            return SanitizeButtonText(text);
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

            // Инициализация состояния ползунка скорости градиента
            if (SpeedG != null)
            {
                if (_settings.IsMonoColor)
                {
                    //SpeedG.Background = System.Windows.Media.Brushes.DarkGray;
                    SpeedG.IsEnabled = false;
                }
                else
                {
                    //SpeedG.Background = System.Windows.Media.Brushes.Black;
                    SpeedG.IsEnabled = true;
                }
            }

            RefreshHostUi();
        }

        private string BuildSettingsPayload()
        {
            string buttonPayload = string.Join("|", new[]
            {
                _settings.M1Value, _settings.M2Value, _settings.M3Value, _settings.M4Value, _settings.M5Value
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

        private void UpdatePasswordChangeButtonState()
        {
            if (ChangePasswordButton == null)
            {
                return;
            }

            if (_verificationLockUntil is DateTime lockUntil && DateTime.Now < lockUntil)
            {
                ChangePasswordButton.IsEnabled = false;
                int remainingSeconds = (int)(lockUntil - DateTime.Now).TotalSeconds;
                ChangePasswordButton.Content = $"Заблокировано ({remainingSeconds}с)";
            }
            else
            {
                ChangePasswordButton.IsEnabled = true;
                ChangePasswordButton.Content = "Сменить пароль";
                _lockUpdateTimer?.Stop();
            }
        }
        private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_verificationLockUntil.HasValue && DateTime.Now < _verificationLockUntil.Value)
            {
                int remainingSeconds = (int)(_verificationLockUntil.Value - DateTime.Now).TotalSeconds;
                MessageBox.Show(
                    $"Функция смены пароля заблокирована.\nПовторите попытку через {remainingSeconds} сек.",
                    "Заблокировано",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_verificationLockUntil.HasValue && DateTime.Now >= _verificationLockUntil.Value)
            {
                _verificationLockUntil = null;
                _failedVerificationAttempts = 0;
            }

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
                PasswordComboDialog verifyDialog = new PasswordComboDialog(
                    "Подтверждение пароля",
                    $"Введите текущий пароль для подтверждения:\n" +
                    $"Попыток осталось: {MaxPasswordChangeAttempts - _failedVerificationAttempts}",
                    "Подтвердить");

                if (verifyDialog.ShowDialog() != true)
                {
                    return;
                }

                string currentPassword = verifyDialog.PasswordCombo;
                if (!IsValidPasswordCombo(currentPassword))
                {
                    MessageBox.Show("Неверный формат пароля.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    continue;
                }

                string? verifyResp = await SendCommandAndAwaitAsync(
                    $"AUTH:{currentPassword}",
                    s => s == "AUTH_OK" || s == "AUTH_FAILED" || s.StartsWith("ERROR", StringComparison.Ordinal));

                if (verifyResp == "AUTH_OK")
                {
                    _failedVerificationAttempts = 0;
                    break;
                }

                // Неверным паролем считаем только явный отказ донгла (AUTH_FAILED).
                // Таймаут или ошибка связи не должны тратить попытку и вести к блокировке.
                if (verifyResp != "AUTH_FAILED")
                {
                    MessageBox.Show(
                        $"Нет ответа от донгла ({verifyResp ?? "таймаут"}).\nПопробуйте ещё раз.",
                        "Ошибка связи",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    continue;
                }

                _failedVerificationAttempts++;

                if (_failedVerificationAttempts >= MaxPasswordChangeAttempts)
                {
                    _verificationLockUntil = DateTime.Now.AddMinutes(PasswordChangeLockMinutes);
                    UpdatePasswordChangeButtonState();
                    _lockUpdateTimer?.Start();
                    MessageBox.Show(
                        $"Превышено количество попыток ввода пароля ({MaxPasswordChangeAttempts}).\n" +
                        $"Функция смены пароля заблокирована на {PasswordChangeLockMinutes} минуту.",
                        "Блокировка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show(
                    $"Неверный пароль.\n" +
                    $"Попыток осталось: {MaxPasswordChangeAttempts - _failedVerificationAttempts}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            while (_isConnected)
            {
                PasswordComboDialog dialog = new PasswordComboDialog(
                    "Смена пароля",
                    "Введите новую комбинацию (только цифры):",
                    "Сохранить");

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                string combo = dialog.PasswordCombo;
                if (!IsValidPasswordCombo(combo))
                {
                    MessageBox.Show("Неверный формат. Используйте только цифры.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void ChangePasswordButton_MouseEnter(object sender, MouseEventArgs e)
        {
            if (ChangePasswordButton.IsEnabled)
            {
                ChangePasswordButton.BorderBrush = System.Windows.Media.Brushes.Blue;
                ChangePasswordButton.BorderThickness = new Thickness(2);
            }
        }

        private void ChangePasswordButton_MouseLeave(object sender, MouseEventArgs e)
        {
            ChangePasswordButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
            ChangePasswordButton.BorderThickness = new Thickness(1);
        }

        //=== Обработчики переключения режимов подсветки ===
        private void MonoColor_Checked(object sender, RoutedEventArgs e)
        {
            if (SpeedG != null)
            {
                //SpeedG.Background = System.Windows.Media.Brushes.DarkGray;
                SpeedG.IsEnabled = false;
            }
        }

        private void MonoColor_Unchecked(object sender, RoutedEventArgs e)
        {
            if (SpeedG != null)
            {
                //SpeedG.Background = System.Windows.Media.Brushes.DarkGray;
                SpeedG.IsEnabled = true;
            }
        }

        private void Gradient_Checked(object sender, RoutedEventArgs e)
        {
            if (SpeedG != null)
            {
                //SpeedG.Background = System.Windows.Media.Brushes.DarkGray;
                SpeedG.IsEnabled = true;
            }
        }

        private void Gradient_Unchecked(object sender, RoutedEventArgs e)
        {
            if (SpeedG != null)
            {
                //SpeedG.Background = System.Windows.Media.Brushes.DarkGray;
                SpeedG.IsEnabled = false;
            }
        }
        // =================================================

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Window helpWindow = new Window
            {
                Title = "Справка о функциях",
                Width = 520,
                Height = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            Grid mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            ScrollViewer scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(15)
            };

            StackPanel contentStack = new StackPanel();

            TextBlock textBlock = new TextBlock
            {
                Text = "Справка по функциям программы\n\n" +
                       "1) Подключение\n" +
                       "- Программа автоматически ищет и подключает донгл манипулятора\n" +
                       "- В выпадающем списке COM-портов можно выбрать порт вручную\n" +
                       "- При отключении донгла программа попытается переподключиться\n\n" +
                       "2) Безопасность\n" +
                       "- После подключения требуется ввод пароля-комбинации (цифры 0-9)\n" +
                       "- Пока пароль не введён, все настройки и команды заблокированы\n" +
                       "- Пароль можно изменить через кнопку \"Сменить пароль\"\n\n" +
                       "3) Курсор\n" +
                       "- Инверсия X/Y - отразить движение курсора по горизонтали/вертикали\n" +
                       "- Чувствительность - скорость реакции сенсора (1-10)\n\n" +
                       "4) Подсветка\n" +
                       "- Яркость - интенсивность подсветки (0-100%)\n" +
                       "- Режим: Градиент (плавная смена цветов) или Моноцвет\n" +
                       "- Цвет RGB - формат 000.000.000 (красный.зелёный.синий)\n" +
                       "- Скорость градиента - быстрота смены цветов (0-100)\n\n" +
                       "5) Клавиши\n" +
                       "- Настройка биндов для кнопок M1-M5\n" +
                       "- Доступные действия: клики мыши, клавиши клавиатуры,\n" +
                       "  мультимедийные команды (left click — левый клик (левая кнопка мыши),\n  " +
                       " right click — правый клик (правая кнопка мыши),\n  " +
                       "middle click — средний клик (колесико мыши),\n  " +
                       "back — назад, forward — вперёд, Volume Up — увеличить громкость,\n  " +
                       " Volume Down — уменьшить громкость,\n  " +
                       "Mute — отключить звук (без звука),\n  " +
                       "Task View — представление задач (переключение задач))\n" +
                       "- Изменение пароля вызывает окно для смены пароля, но при 3 ошибках\n" +
                       "  приложение будет заблокировано,\n  и после таймера необходимо повторно авторизоваться через эту кнопку",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 20)
            };
            contentStack.Children.Add(textBlock);

            try
            {
                Image helpImage = new Image
                {
                    Stretch = Stretch.Uniform,
                    MaxWidth = 400,
                    MaxHeight = 300,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(10, 10, 10, 20)
                };

                helpImage.Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Resources/help_image.png", UriKind.Absolute));

                contentStack.Children.Add(helpImage);
            }
            catch
            {
                TextBlock errorText = new TextBlock
                {
                    Text = "[Изображение не загружено]",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 20)
                };
                contentStack.Children.Add(errorText);
            }

            TextBlock textBlock2 = new TextBlock
            {
                Text = "6) Кнопки управления\n" +
                       "- Сохранить - отправить текущие настройки на донгл\n" +
                       "- Сбросить - загрузить настройки из донгла в программу\n" +
                       "- Сменить пароль - установить новую комбинацию доступа\n\n" +
                       "7) Лог активности\n" +
                       "- Показывает все команды и ответы донгла\n" +
                       "- TX - отправленные команды\n" +
                       "- RX - полученные ответы\n" +
                       "- ERROR - ошибки связи",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 10)
            };
            contentStack.Children.Add(textBlock2);

            scrollViewer.Content = contentStack;
            Grid.SetRow(scrollViewer, 0);

            Button closeButton = new Button
            {
                Content = "Закрыть",
                Width = 100,
                Margin = new Thickness(0, 10, 15, 15),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            closeButton.Click += (_, _) => helpWindow.Close();
            Grid.SetRow(closeButton, 1);

            mainGrid.Children.Add(scrollViewer);
            mainGrid.Children.Add(closeButton);
            helpWindow.Content = mainGrid;
            helpWindow.ShowDialog();
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
            _lockUpdateTimer?.Stop();
            DisconnectFromPort();
        }

        #endregion

        #region Host actions

        private static HostActionConfig[] CreateEmptyHostActions()
        {
            var actions = new HostActionConfig[9];
            for (int i = 0; i < actions.Length; i++)
            {
                actions[i] = new HostActionConfig();
            }

            return actions;
        }

        private void ConfigureHostAction(int index, string buttonLabel)
        {
            if (index < 0 || index >= _hostActions.Length)
            {
                return;
            }

            HostActionDialog dialog = new HostActionDialog(buttonLabel, _hostActions[index]) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _hostActions[index] = dialog.Result;

            // Если действие снято, а в поле бинда остался маркер "host",
            // вернём редактируемое значение по умолчанию.
            if (_hostActions[index].Type == HostActionType.None)
            {
                TextBox? bindBox = GetBindBox(index);
                if (bindBox != null && string.Equals(bindBox.Text?.Trim(), "host", StringComparison.OrdinalIgnoreCase))
                {
                    bindBox.Text = DefaultBindFor(index);
                }
            }

            SaveHostActions();
            RefreshHostUi();

            if (_hostActions[index].Type != HostActionType.None)
            {
                Log($"{buttonLabel}: назначено действие ПК ({DescribeHostAction(_hostActions[index])}). " +
                    "Нажмите «Сохранить», чтобы применить на донгле.", "INFO");

                if (index >= PhysicalButtonCount)
                {
                    Log($"{buttonLabel}: на текущем железе нет физической кнопки — " +
                        "действие сохранено, но сработает только после доработки мыши.", "WARNING");
                }
            }
            else
            {
                Log($"{buttonLabel}: действие ПК снято. Нажмите «Сохранить», чтобы применить.", "INFO");
            }
        }

        private TextBox? GetBindBox(int index)
        {
            return index switch
            {
                0 => M1,
                1 => M2,
                2 => M3,
                3 => M4,
                4 => M5,
                _ => null
            };
        }

        private static string DefaultBindFor(int index)
        {
            return index switch
            {
                0 => "left click",
                1 => "right click",
                2 => "middle click",
                3 => "back",
                4 => "forward",
                _ => "none"
            };
        }

        private void RefreshHostUi()
        {
            RefreshHostRow(0, M1, M1HostButton);
            RefreshHostRow(1, M2, M2HostButton);
            RefreshHostRow(2, M3, M3HostButton);
            RefreshHostRow(3, M4, M4HostButton);
            RefreshHostRow(4, M5, M5HostButton);
        }

        private void RefreshHostRow(int index, TextBox? bindBox, Button? hostButton)
        {
            if (bindBox == null || hostButton == null)
            {
                return;
            }

            HostActionConfig action = _hostActions[index];
            bool isHost = action.Type != HostActionType.None;
            bool deviceSaysHost = string.Equals(bindBox.Text?.Trim(), "host", StringComparison.OrdinalIgnoreCase);

            // Поле бинда не используется, когда на кнопку назначено действие ПК.
            bindBox.IsEnabled = _isAuthenticated && !isHost && !deviceSaysHost;

            if (isHost)
            {
                hostButton.Content = "Изменено ✓";
                hostButton.ToolTip = DescribeHostAction(action);
            }
            else if (deviceSaysHost)
            {
                hostButton.Content = "Изменено ?";
                hostButton.ToolTip = "На донгле кнопка помечена как действие пользовательское, но действие не настроено на этом компьютере. Нажмите, чтобы задать.";
            }
            else
            {
                hostButton.Content = "Измененить";
                hostButton.ToolTip = "Назначить действие на ПК (запуск программы или показ текста)";
            }
        }

        private static string DescribeHostAction(HostActionConfig action)
        {
            return action.Type switch
            {
                HostActionType.Program => $"Программа: {action.Payload}",
                HostActionType.Text => $"Текст: {ShortenText(action.Payload)}",
                HostActionType.Url => $"Сайт: {action.Payload}",
                _ => "Нет действия"
            };
        }

        private static string ShortenText(string text)
        {
            string single = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return single.Length <= 40 ? single : single.Substring(0, 40) + "…";
        }

        private void ExecuteHostAction(int oneBasedIndex)
        {
            int index = oneBasedIndex - 1;
            if (index < 0 || index >= _hostActions.Length)
            {
                return;
            }

            HostActionConfig action = _hostActions[index];
            if (action.Type == HostActionType.None || string.IsNullOrWhiteSpace(action.Payload))
            {
                Log($"BTN:{oneBasedIndex} — действие ПК не настроено", "WARNING");
                return;
            }

            try
            {
                switch (action.Type)
                {
                    case HostActionType.Program:
                        Process.Start(new ProcessStartInfo { FileName = action.Payload, UseShellExecute = true });
                        Log($"M{oneBasedIndex}: запуск программы {action.Payload}", "SUCCESS");
                        break;

                    case HostActionType.Url:
                        Process.Start(new ProcessStartInfo { FileName = action.Payload, UseShellExecute = true });
                        Log($"M{oneBasedIndex}: открытие сайта {action.Payload}", "SUCCESS");
                        break;

                    case HostActionType.Text:
                        TypeTextAtCursor(action.Payload);
                        Log($"M{oneBasedIndex}: текст вставлен в активное окно", "SUCCESS");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Ошибка действия M{oneBasedIndex}: {ex.Message}", "ERROR");
                MessageBox.Show($"Не удалось выполнить действие кнопки M{oneBasedIndex}:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string GetHostActionsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CBTM");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "hostactions.json");
        }

        private void LoadHostActions()
        {
            try
            {
                string path = GetHostActionsPath();
                if (!File.Exists(path))
                {
                    return;
                }

                HostActionConfig[]? data = JsonSerializer.Deserialize<HostActionConfig[]>(File.ReadAllText(path), HostJsonOptions);
                if (data == null)
                {
                    return;
                }

                for (int i = 0; i < _hostActions.Length && i < data.Length; i++)
                {
                    _hostActions[i] = data[i] ?? new HostActionConfig();
                }
            }
            catch (Exception ex)
            {
                Log($"Не удалось загрузить действия ПК: {ex.Message}", "WARNING");
            }
        }

        private void SaveHostActions()
        {
            try
            {
                File.WriteAllText(GetHostActionsPath(), JsonSerializer.Serialize(_hostActions, HostJsonOptions));
            }
            catch (Exception ex)
            {
                Log($"Не удалось сохранить действия ПК: {ex.Message}", "WARNING");
            }
        }

        #endregion

        #region Text typing (SendInput)

        // Печатает текст в окно/поле, у которого сейчас фокус ввода (где стоит курсор),
        // эмулируя Unicode-нажатия клавиш через WinAPI SendInput.
        private static void TypeTextAtCursor(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var inputs = new List<INPUT>(text.Length * 2);

            foreach (char c in text)
            {
                if (c == '\r')
                {
                    // Перевод строки печатаем как Enter ниже (по '\n'); одиночный '\r' пропускаем.
                    continue;
                }

                if (c == '\n')
                {
                    inputs.Add(MakeKeyboardInput('\0', VK_RETURN, keyUp: false, unicode: false));
                    inputs.Add(MakeKeyboardInput('\0', VK_RETURN, keyUp: true, unicode: false));
                    continue;
                }

                // Обычный символ (в т.ч. часть суррогатной пары) — как Unicode-нажатие.
                inputs.Add(MakeKeyboardInput(c, 0, keyUp: false, unicode: true));
                inputs.Add(MakeKeyboardInput(c, 0, keyUp: true, unicode: true));
            }

            if (inputs.Count == 0)
            {
                return;
            }

            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            if (sent != inputs.Count)
            {
                throw new InvalidOperationException($"SendInput отправил {sent} из {inputs.Count} событий (код {Marshal.GetLastWin32Error()}).");
            }
        }

        private static INPUT MakeKeyboardInput(char unicodeChar, ushort virtualKey, bool keyUp, bool unicode)
        {
            uint flags = 0;
            if (unicode) flags |= KEYEVENTF_UNICODE;
            if (keyUp) flags |= KEYEVENTF_KEYUP;

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = unicode ? (ushort)0 : virtualKey,
                        wScan = unicode ? unicodeChar : (ushort)0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const ushort VK_RETURN = 0x0D;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
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
            Title = $"CRM - Управление манипулятором | {message}";
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
            _comboBox.PreviewTextInput += (_, e) => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
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
            if (!Regex.IsMatch(value, "^[0-9]{1,16}$"))
            {
                MessageBox.Show("Комбинация должна содержать только цифры.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PasswordCombo = value;
            DialogResult = true;
        }
    }

    // Диалог настройки действия ПК для одной кнопки: тип + значение (+ обзор файла).
    public class HostActionDialog : Window
    {
        private readonly ComboBox _typeBox;
        private readonly TextBox _valueBox;
        private readonly Button _browseButton;
        private readonly TextBlock _hint;

        public HostActionConfig Result { get; private set; } = new HostActionConfig();

        public HostActionDialog(string buttonLabel, HostActionConfig current)
        {
            Title = $"Действие ПК — {buttonLabel}";
            Width = 480;
            Height = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            Grid root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock typeLabel = new TextBlock { Text = "Тип действия:", Margin = new Thickness(0, 0, 0, 4) };
            Grid.SetRow(typeLabel, 0);

            StackPanel typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(typeRow, 1);

            _typeBox = new ComboBox { Width = 220 };
            _typeBox.Items.Add("Нет действия");
            _typeBox.Items.Add("Запуск программы / файла");
            _typeBox.Items.Add("Печать текста (где курсор)");
            _typeBox.Items.Add("Открытие сайта (URL)");
            _typeBox.SelectedIndex = (int)current.Type;
            _typeBox.SelectionChanged += (_, _) => UpdateState();

            _browseButton = new Button { Content = "Обзор…", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
            _browseButton.Click += (_, _) => Browse();

            typeRow.Children.Add(_typeBox);
            typeRow.Children.Add(_browseButton);

            _valueBox = new TextBox
            {
                Text = current.Payload ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(_valueBox, 2);

            _hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 8)
            };

            StackPanel bottom = new StackPanel { Orientation = Orientation.Vertical };
            Grid.SetRow(bottom, 3);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            Button ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, _) => Submit();
            Button cancel = new Button { Content = "Отмена", Width = 90 };
            cancel.Click += (_, _) => DialogResult = false;

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            bottom.Children.Add(_hint);
            bottom.Children.Add(buttons);

            root.Children.Add(typeLabel);
            root.Children.Add(typeRow);
            root.Children.Add(_valueBox);
            root.Children.Add(bottom);

            Content = root;
            UpdateState();
        }

        private HostActionType SelectedType => (HostActionType)Math.Max(0, _typeBox.SelectedIndex);

        private void UpdateState()
        {
            HostActionType type = SelectedType;
            _browseButton.IsEnabled = type == HostActionType.Program;
            _valueBox.IsEnabled = type != HostActionType.None;

            _hint.Text = type switch
            {
                HostActionType.Program => "Укажите путь к программе или файлу. Нажмите «Обзор…», чтобы выбрать.",
                HostActionType.Text => "Введите текст — он будет напечатан туда, где стоит курсор ввода, при нажатии кнопки.",
                HostActionType.Url => "Введите адрес сайта, например https://example.com",
                _ => "Действие ПК для этой кнопки отключено (кнопка работает как обычный бинд)."
            };
        }

        private void Browse()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите программу или файл",
                Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                _valueBox.Text = dialog.FileName;
            }
        }

        private void Submit()
        {
            HostActionType type = SelectedType;
            string payload = (_valueBox.Text ?? string.Empty).Trim();

            if (type != HostActionType.None && string.IsNullOrWhiteSpace(payload))
            {
                MessageBox.Show("Введите значение для выбранного действия.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = new HostActionConfig
            {
                Type = type,
                Payload = type == HostActionType.None ? string.Empty : payload
            };
            DialogResult = true;
        }
    }
}