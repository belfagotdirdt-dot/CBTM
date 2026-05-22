#include <Mouse.h>
#include <Arduino.h>
#include <RF24.h>
#include <math.h>
#include <string.h>

struct MousePacket {
  uint8_t magic;
  uint8_t version;
  uint8_t seq;
  int8_t dx;
  int8_t dy;
  int8_t wheel;
  uint8_t buttons;
  uint8_t checksum;
};

struct DeviceSettings {
    bool invertX;
    bool invertY;
    char sensitivity[8];
    uint8_t brightness;
    uint8_t isGradient;
    char color[16];
    uint8_t gradientSpeed;
    char buttons[9][24];
    uint8_t passwordBindButton; // 123 / 312 | 1 - button 1; 2 - button 2; 3 - button 3;
};

struct SettingRGBLedMouse {
    uint8_t magic;
    uint8_t version;
    uint8_t seq;
    uint8_t brightness;
    uint8_t isGradient;
    uint32_t color;
    uint8_t gradientSpeed;
    uint8_t checksum;
};

RF24 radio(7, 8); // CE, CSN
const byte address[6] = "00001";

constexpr uint8_t BTN_RIGHT_MASK = (1 << 6);
constexpr uint8_t BTN_MIDDLE_MASK = (1 << 4);
constexpr uint8_t BTN_LEFT_MASK = (1 << 2);
constexpr uint8_t LED_SETTINGS_MAGIC = 0xC7;
constexpr uint8_t LED_SETTINGS_VERSION = 1;
constexpr uint32_t RX_TIMEOUT_MS = 120;

#define SERIAL_BUFFER_SIZE 320
#define PASSWORD_BUFFER_SIZE 24

DeviceSettings g_settings{};
char g_serialLine[SERIAL_BUFFER_SIZE];
size_t g_serialLineIndex = 0;
char g_passwordCombo[PASSWORD_BUFFER_SIZE] = {0};
bool g_passwordIsSet = false;
bool g_isAuthenticated = false;

enum MouseAction : uint8_t {
  ACTION_NONE = 0,
  ACTION_LEFT,
  ACTION_RIGHT,
  ACTION_MIDDLE,
};

MouseAction g_buttonMapLeft = ACTION_LEFT;
MouseAction g_buttonMapRight = ACTION_RIGHT;
MouseAction g_buttonMapMiddle = ACTION_MIDDLE;

uint8_t prevMappedButtons = 0;
uint32_t lastPacketMs = 0;
uint8_t ledSettingsSeq = 0;

constexpr uint8_t MAPPED_LEFT_MASK = (1 << 0);
constexpr uint8_t MAPPED_RIGHT_MASK = (1 << 1);
constexpr uint8_t MAPPED_MIDDLE_MASK = (1 << 2);

bool parseBoolToken(const String &raw, bool defaultValue = false) {
  String token = raw;
  token.trim();
  token.toLowerCase();

  if (token == "1" || token == "true" || token == "on" || token == "yes") {
    return true;
  }
  if (token == "0" || token == "false" || token == "off" || token == "no") {
    return false;
  }
  return defaultValue;
}

MouseAction parseMouseAction(const char *rawLabel, MouseAction fallback) {
  String label = String(rawLabel);
  label.trim();
  label.toLowerCase();

  if (label == "left click") {
    return ACTION_LEFT;
  }
  if (label == "right click") {
    return ACTION_RIGHT;
  }
  if (label == "middle click") {
    return ACTION_MIDDLE;
  }
  return fallback;
}

void rebuildButtonMap() {
  g_buttonMapLeft = parseMouseAction(g_settings.buttons[0], ACTION_LEFT);
  g_buttonMapRight = parseMouseAction(g_settings.buttons[1], ACTION_RIGHT);
  g_buttonMapMiddle = parseMouseAction(g_settings.buttons[2], ACTION_MIDDLE);
}

uint8_t actionToMappedMask(const MouseAction action) {
  if (action == ACTION_LEFT) {
    return MAPPED_LEFT_MASK;
  }
  if (action == ACTION_RIGHT) {
    return MAPPED_RIGHT_MASK;
  }
  if (action == ACTION_MIDDLE) {
    return MAPPED_MIDDLE_MASK;
  }
  return 0;
}

void syncMappedButton(const uint8_t newMappedButtons, const uint8_t mappedMask, const uint8_t mouseButton) {
  const bool wasPressed = (prevMappedButtons & mappedMask) != 0;
  const bool nowPressed = (newMappedButtons & mappedMask) != 0;

  if (!wasPressed && nowPressed) {
    Mouse.press(mouseButton);
  } else if (wasPressed && !nowPressed) {
    Mouse.release(mouseButton);
  }
}

uint8_t mapPhysicalButtonsToMouse(const uint8_t physicalButtons) {
  uint8_t mapped = 0;

  if ((physicalButtons & BTN_LEFT_MASK) != 0) {
    mapped |= actionToMappedMask(g_buttonMapLeft);
  }
  if ((physicalButtons & BTN_RIGHT_MASK) != 0) {
    mapped |= actionToMappedMask(g_buttonMapRight);
  }
  if ((physicalButtons & BTN_MIDDLE_MASK) != 0) {
    mapped |= actionToMappedMask(g_buttonMapMiddle);
  }

  return mapped;
}

uint8_t calcLedSettingsChecksum(const SettingRGBLedMouse &p) {
  return static_cast<uint8_t>(
      p.magic ^ p.version ^ p.seq ^ p.brightness ^ p.isGradient ^
      static_cast<uint8_t>(p.color >> 24) ^ static_cast<uint8_t>(p.color >> 16) ^
      static_cast<uint8_t>(p.color >> 8) ^ static_cast<uint8_t>(p.color) ^
      p.gradientSpeed);
}

uint32_t parseRgbColor(const char *colorStr) {
  String color = String(colorStr);
  color.trim();

  int firstDot = color.indexOf('.');
  int secondDot = color.indexOf('.', firstDot + 1);
  if (firstDot <= 0 || secondDot <= firstDot + 1) {
    return 0;
  }

  int r = color.substring(0, firstDot).toInt();
  int g = color.substring(firstDot + 1, secondDot).toInt();
  int b = color.substring(secondDot + 1).toInt();

  r = constrain(r, 0, 255);
  g = constrain(g, 0, 255);
  b = constrain(b, 0, 255);

  return (static_cast<uint32_t>(r) << 16) |
         (static_cast<uint32_t>(g) << 8) |
         static_cast<uint32_t>(b);
}

void queueLedSettingsAckPayload() {
  SettingRGBLedMouse packet{};
  packet.magic = LED_SETTINGS_MAGIC;
  packet.version = LED_SETTINGS_VERSION;
  packet.seq = ledSettingsSeq++;
  packet.brightness = g_settings.brightness;
  packet.isGradient = g_settings.isGradient ? 1 : 0;
  packet.color = parseRgbColor(g_settings.color);
  packet.gradientSpeed = g_settings.gradientSpeed;
  packet.checksum = calcLedSettingsChecksum(packet);

  radio.writeAckPayload(0, &packet, sizeof(packet));
}

void safeCopy(char *dst, size_t dstSize, const String &value) {
  if (dstSize == 0) {
    return;
  }

  size_t n = value.length();
  if (n >= dstSize) {
    n = dstSize - 1;
  }

  memcpy(dst, value.c_str(), n);
  dst[n] = '\0';
}

bool isValidPasswordCombo(const String &comboRaw) {
  String combo = comboRaw;
  combo.trim();

  if (combo.length() <= 0 || combo.length() >= PASSWORD_BUFFER_SIZE) {
    return false;
  }

  for (int i = 0; i < combo.length(); ++i) {
    const char c = combo[i];
    if (c != '1' && c != '2' && c != '3') {
      return false;
    }
  }

  return true;
}

bool passwordMatches(const String &comboRaw) {
  String combo = comboRaw;
  combo.trim();
  return strcmp(g_passwordCombo, combo.c_str()) == 0;
}

void setPassword(const String &comboRaw) {
  safeCopy(g_passwordCombo, PASSWORD_BUFFER_SIZE, comboRaw);
  g_passwordIsSet = true;
  g_isAuthenticated = true;
}

bool isAuthRequired() {
  return g_passwordIsSet && !g_isAuthenticated;
}

void setDefaultSettings() {
  g_settings.invertX = 0;
  g_settings.invertY = 0;
  safeCopy(g_settings.sensitivity, sizeof(g_settings.sensitivity), "1");
  g_settings.brightness = 50;
  g_settings.isGradient = 1;
  safeCopy(g_settings.color, sizeof(g_settings.color), "000.000.000");
  g_settings.gradientSpeed = 50;

  const char *defaults[9] = {
    "left click",
    "right click",
    "middle click",
    "back",
    "forward",
    "Volume Up",
    "Volume Down",
    "Mute",
    "Task View"
  };

  for (int i = 0; i < 9; ++i) {
    safeCopy(g_settings.buttons[i], sizeof(g_settings.buttons[i]), String(defaults[i]));
  }

  rebuildButtonMap();

  g_passwordCombo[0] = '\0';
  g_passwordIsSet = false;
  g_isAuthenticated = false;
}

int clampMouseValue(int value) {
  if (value > 127) {
    return 127;
  }
  if (value < -127) {
    return -127;
  }
  return value;
}

float getSensitivityMultiplier() {
  float s = atof(g_settings.sensitivity);
  if (s < 0.1f) {
    s = 1.0f;
  }
  if (s > 10.0f) {
    s = 10.0f;
  }
  return s;
}

void sendSettingsToSerial() {
  Serial.print("SETTINGS:");
  Serial.print(g_settings.invertX ? "1" : "0");
  Serial.print(',');
  Serial.print(g_settings.invertY ? "1" : "0");
  Serial.print(',');
  Serial.print(g_settings.sensitivity);
  Serial.print(',');
  Serial.print(g_settings.brightness);
  Serial.print(',');
  Serial.print(g_settings.isGradient ? "1" : "0");
  Serial.print(',');
  Serial.print(g_settings.color);
  Serial.print(',');
  Serial.print(g_settings.gradientSpeed);
  Serial.print(',');

  for (int i = 0; i < 9; ++i) {
    if (i > 0) {
      Serial.print('|');
    }
    Serial.print(g_settings.buttons[i]);
  }

  Serial.println();
}

bool parseSettingsPayload(const String &payload) {
  String parts[8];
  int partCount = 0;
  int start = 0;

  for (int i = 0; i < payload.length() && partCount < 7; ++i) {
    if (payload[i] == ',') {
      parts[partCount++] = payload.substring(start, i);
      start = i + 1;
    }
  }

  if (partCount != 7) {
    return false;
  }

  parts[partCount++] = payload.substring(start);
  if (partCount < 8) {
    return false;
  }

  for (int i = 0; i < 8; ++i) {
    parts[i].trim();
  }

  g_settings.invertX = parseBoolToken(parts[0], g_settings.invertX);
  g_settings.invertY = parseBoolToken(parts[1], g_settings.invertY);
  safeCopy(g_settings.sensitivity, sizeof(g_settings.sensitivity), parts[2]);

  int brightness = parts[3].toInt();
  if (brightness < 0) brightness = 0;
  if (brightness > 100) brightness = 100;
  g_settings.brightness = static_cast<uint8_t>(brightness);

  g_settings.isGradient = parseBoolToken(parts[4], g_settings.isGradient) ? 1 : 0;
  safeCopy(g_settings.color, sizeof(g_settings.color), parts[5]);

  int gradientSpeed = parts[6].toInt();
  if (gradientSpeed < 0) gradientSpeed = 0;
  if (gradientSpeed > 100) gradientSpeed = 100;
  g_settings.gradientSpeed = static_cast<uint8_t>(gradientSpeed);

  String buttonBlock = parts[7];
  int buttonIndex = 0;
  int buttonStart = 0;

  while (buttonIndex < 9) {
    int nextSep = buttonBlock.indexOf('|', buttonStart);
    String buttonValue;

    if (nextSep == -1) {
      buttonValue = buttonBlock.substring(buttonStart);
    } else {
      buttonValue = buttonBlock.substring(buttonStart, nextSep);
    }

    buttonValue.trim();
    safeCopy(g_settings.buttons[buttonIndex], sizeof(g_settings.buttons[buttonIndex]), buttonValue);

    ++buttonIndex;
    if (nextSep == -1) {
      break;
    }

    buttonStart = nextSep + 1;
  }

  while (buttonIndex < 9) {
    g_settings.buttons[buttonIndex][0] = '\0';
    ++buttonIndex;
  }

  rebuildButtonMap();

  return true;
}

void handleSerialCommand(const String &rawCmd) {
  String cmd = rawCmd;
  cmd.trim();

  if (cmd.length() == 0) {
    return;
  }

  if (cmd == "IDENTIFY") {
    Serial.println("MOUSEBOYS_DONGLE");
  } else if (cmd == "PING") {
    Serial.println("PONG");
  } else if (cmd == "GET_AUTH_STATE") {
    if (!g_passwordIsSet) {
      Serial.println("NO_PASSWORD");
    } else if (g_isAuthenticated) {
      Serial.println("AUTH_OK");
    } else {
      Serial.println("AUTH_REQUIRED");
    }
  } else if (cmd.startsWith("AUTH:")) {
    String payload = cmd.substring(5);
    payload.trim();

    if (!g_passwordIsSet) {
      g_isAuthenticated = true;
      Serial.println("AUTH_OK");
    } else if (passwordMatches(payload)) {
      g_isAuthenticated = true;
      Serial.println("AUTH_OK");
    } else {
      g_isAuthenticated = false;
      Serial.println("AUTH_FAILED");
    }
  } else if (cmd.startsWith("SET_PASSWORD:")) {
    if (g_passwordIsSet && !g_isAuthenticated) {
      Serial.println("ERROR:NOT_AUTHENTICATED");
      return;
    }

    String payload = cmd.substring(13);
    payload.trim();

    if (!isValidPasswordCombo(payload)) {
      Serial.println("ERROR:BAD_PASSWORD_FORMAT");
      return;
    }

    setPassword(payload);
    Serial.println("PASSWORD_CHANGED");
  } else if (cmd == "GET_SETTINGS") {
    if (isAuthRequired()) {
      Serial.println("ERROR:NOT_AUTHENTICATED");
    } else {
      sendSettingsToSerial();
    }
  } else if (cmd.startsWith("SET_SETTINGS:")) {
    if (isAuthRequired()) {
      Serial.println("ERROR:NOT_AUTHENTICATED");
      return;
    }

    String payload = cmd.substring(13);
    if (parseSettingsPayload(payload)) {
      queueLedSettingsAckPayload();
      Serial.println("OK");
    } else {
      Serial.println("ERROR:BAD_FORMAT");
    }
  } else {
    Serial.println("ERROR:UNKNOWN_COMMAND");
  }
}

void pollSerial() {
  while (Serial.available() > 0) {
    char c = static_cast<char>(Serial.read());

    if (c == '\r') {
      continue;
    }

    if (c == '\n') {
      if (g_serialLineIndex > 0) {
        g_serialLine[g_serialLineIndex] = '\0';
        handleSerialCommand(String(g_serialLine));
        g_serialLineIndex = 0;
      }
      continue;
    }

    if (g_serialLineIndex < SERIAL_BUFFER_SIZE - 1) {
      g_serialLine[g_serialLineIndex++] = c;
    } else {
      g_serialLineIndex = 0;
    }
  }
}

void releaseAllButtons() {
  Mouse.release(MOUSE_LEFT);
  Mouse.release(MOUSE_RIGHT);
  Mouse.release(MOUSE_MIDDLE);
}

void setup() {
  Serial.begin(9600);
  setDefaultSettings();

  radio.begin();
  radio.openReadingPipe(0, address);
  radio.setPALevel(RF24_PA_LOW);
  radio.setDataRate(RF24_1MBPS);
  radio.enableDynamicPayloads();
  radio.enableAckPayload();
  radio.startListening();

  Mouse.begin();
  lastPacketMs = millis();
}

void loop() {
  pollSerial();

  // Пока не пройдена авторизация, любые действия мыши с донгла запрещены.
  if (isAuthRequired()) {
    if (prevMappedButtons != 0) {
      prevMappedButtons = 0;
      releaseAllButtons();
    }

    while (radio.available()) {
      MousePacket ignored{};
      radio.read(&ignored, sizeof(ignored));
      lastPacketMs = millis();
    }

    delay(5);
    return;
  }

  if (radio.available()) {
    MousePacket p{};
    radio.read(&p, sizeof(p));
    lastPacketMs = millis();

    const uint8_t mappedButtons = mapPhysicalButtonsToMouse(p.buttons);
    syncMappedButton(mappedButtons, MAPPED_RIGHT_MASK, MOUSE_RIGHT);
    syncMappedButton(mappedButtons, MAPPED_MIDDLE_MASK, MOUSE_MIDDLE);
    syncMappedButton(mappedButtons, MAPPED_LEFT_MASK, MOUSE_LEFT);
    prevMappedButtons = mappedButtons;

    float sensitivity = getSensitivityMultiplier();
    int dx = p.dx;
    int dy = p.dy;

    if (g_settings.invertX) {
      dx = -dx;
    }
    if (g_settings.invertY) {
      dy = -dy;
    }

    dx = clampMouseValue(static_cast<int>(roundf(dx * sensitivity)));
    dy = clampMouseValue(static_cast<int>(roundf(dy * sensitivity)));

    if (dx != 0 || dy != 0 || p.wheel != 0) {
      Mouse.move(dx, dy, p.wheel);
    }
  }

  if (prevMappedButtons != 0 && (millis() - lastPacketMs > RX_TIMEOUT_MS)) {
    prevMappedButtons = 0;
    releaseAllButtons();
  }

  delay(5);
}
