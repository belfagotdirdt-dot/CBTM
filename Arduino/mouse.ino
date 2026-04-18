#include <EEPROM.h>
#include <RF24.h>
#include <SPI.h>
#include <avr/io.h>
#include <avr/interrupt.h>

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

constexpr uint8_t PIN_ENC_A  = 2;
constexpr uint8_t PIN_ENC_B  = 3;
constexpr uint8_t PIN_JOY_X  = A0;
constexpr uint8_t PIN_JOY_Y  = A1;
constexpr uint8_t PIN_JOY_SW = 4;
constexpr uint8_t PIN_RIGHT  = 7;
constexpr uint8_t PIN_MIDDLE = 8;
constexpr uint8_t PIN_LEFT   = 6;
constexpr uint8_t PIN_LED_R  = A2;
constexpr uint8_t PIN_LED_G  = A3;
constexpr uint8_t PIN_LED_B  = A5;
constexpr int8_t ENCODER_STEPS_PER_DETENT = 4;
constexpr int16_t JOY_DEADZONE = 70;
constexpr int8_t JOY_MAX_SPEED = 8;
constexpr bool JOY_INVERT_X = true;
constexpr bool JOY_INVERT_Y = true;
constexpr uint8_t LED_SETTINGS_MAGIC = 0xC7;
constexpr uint8_t LED_SETTINGS_VERSION = 1;
constexpr bool LED_COMMON_ANODE = false;
constexpr uint8_t LED_PWM_MAX = 63;
constexpr uint8_t EEPROM_LED_MARKER = 0x4C;
constexpr int EEPROM_LED_ADDR = 0;

struct PersistedLedSettings {
    uint8_t marker;
    SettingRGBLedMouse settings;
    uint8_t markerXor;
};

constexpr uint8_t BTN_RIGHT_MASK  = (1 << 6);
constexpr uint8_t BTN_MIDDLE_MASK = (1 << 4);
constexpr uint8_t BTN_LEFT_MASK   = (1 << 2);

volatile uint8_t encoderPrevState = 0;
volatile int8_t encoderAccumulator = 0;
volatile int16_t encoderWheelPending = 0;
int16_t joyCenterX = 512;
int16_t joyCenterY = 512;

RF24 radio(9, 10);  // CE, CSN

constexpr byte address[6] = "00001";

SettingRGBLedMouse g_ledSettings{};
bool g_hasLedSettings = false;
volatile uint8_t g_ledDutyR = 0;
volatile uint8_t g_ledDutyG = 0;
volatile uint8_t g_ledDutyB = 0;

bool ledSettingsEquivalent(const SettingRGBLedMouse &a, const SettingRGBLedMouse &b) {
    return a.magic == b.magic &&
           a.version == b.version &&
           a.brightness == b.brightness &&
           a.isGradient == b.isGradient &&
           a.color == b.color &&
           a.gradientSpeed == b.gradientSpeed &&
           a.checksum == b.checksum;
}

uint8_t calcLedSettingsChecksum(const SettingRGBLedMouse &p) {
    return static_cast<uint8_t>(
        p.magic ^ p.version ^ p.seq ^ p.brightness ^ p.isGradient ^
        static_cast<uint8_t>(p.color >> 24) ^ static_cast<uint8_t>(p.color >> 16) ^
        static_cast<uint8_t>(p.color >> 8) ^ static_cast<uint8_t>(p.color) ^
        p.gradientSpeed);
}

uint8_t toLedDuty(const uint8_t value) {
    return static_cast<uint8_t>((static_cast<uint16_t>(value) * LED_PWM_MAX + 127) / 255);
}

void setLedDuty(const uint8_t r, const uint8_t g, const uint8_t b) {
    uint8_t dutyR = toLedDuty(r);
    uint8_t dutyG = toLedDuty(g);
    uint8_t dutyB = toLedDuty(b);

    if (LED_COMMON_ANODE) {
        dutyR = LED_PWM_MAX - dutyR;
        dutyG = LED_PWM_MAX - dutyG;
        dutyB = LED_PWM_MAX - dutyB;
    }

    noInterrupts();
    g_ledDutyR = dutyR;
    g_ledDutyG = dutyG;
    g_ledDutyB = dutyB;
    interrupts();
}

void hueToRgb(uint8_t hue, uint8_t &r, uint8_t &g, uint8_t &b) {
    if (hue < 85) {
        r = static_cast<uint8_t>(255 - hue * 3);
        g = static_cast<uint8_t>(hue * 3);
        b = 0;
    } else if (hue < 170) {
        hue = static_cast<uint8_t>(hue - 85);
        r = 0;
        g = static_cast<uint8_t>(255 - hue * 3);
        b = static_cast<uint8_t>(hue * 3);
    } else {
        hue = static_cast<uint8_t>(hue - 170);
        r = static_cast<uint8_t>(hue * 3);
        g = 0;
        b = static_cast<uint8_t>(255 - hue * 3);
    }
}

void updateRgbLedOutput() {
    static uint8_t gradientHue = 0;
    static uint32_t lastGradientTickMs = 0;

    if (!g_hasLedSettings) {
        setLedDuty(0, 0, 0);
        return;
    }

    uint8_t brightness = g_ledSettings.brightness;
    if (brightness > 100) {
        brightness = 100;
    }
    if (brightness == 0) {
        setLedDuty(0, 0, 0);
        return;
    }

    uint8_t r = static_cast<uint8_t>((g_ledSettings.color >> 16) & 0xFF);
    uint8_t g = static_cast<uint8_t>((g_ledSettings.color >> 8) & 0xFF);
    uint8_t b = static_cast<uint8_t>(g_ledSettings.color & 0xFF);

    if (g_ledSettings.isGradient != 0) {
        uint8_t speed = g_ledSettings.gradientSpeed;
        if (speed > 100) {
            speed = 100;
        }

        const uint16_t tickMs = static_cast<uint16_t>(105 - speed);
        const uint32_t nowMs = millis();
        if (nowMs - lastGradientTickMs >= tickMs) {
            gradientHue++;
            lastGradientTickMs = nowMs;
        }

        hueToRgb(gradientHue, r, g, b);
    }

    r = static_cast<uint8_t>((static_cast<uint16_t>(r) * brightness) / 100);
    g = static_cast<uint8_t>((static_cast<uint16_t>(g) * brightness) / 100);
    b = static_cast<uint8_t>((static_cast<uint16_t>(b) * brightness) / 100);
    setLedDuty(r, g, b);
}

void setupLedPwm() {
    pinMode(PIN_LED_R, OUTPUT);
    pinMode(PIN_LED_G, OUTPUT);
    pinMode(PIN_LED_B, OUTPUT);
    digitalWrite(PIN_LED_R, LOW);
    digitalWrite(PIN_LED_G, LOW);
    digitalWrite(PIN_LED_B, LOW);

    cli();
    TCCR2A = 0;
    TCCR2B = 0;
    TCNT2 = 0;
    TCCR2B |= (1 << CS21);  // Prescaler 8 -> overflow ~7.8kHz
    TIMSK2 |= (1 << TOIE2);
    sei();
}

ISR(TIMER2_OVF_vect) {
    static uint8_t phase = 0;
    phase = static_cast<uint8_t>((phase + 1) & LED_PWM_MAX);

    uint8_t out = PORTC;
    if (g_ledDutyR > phase) {
        out |= _BV(PC2);
    } else {
        out &= static_cast<uint8_t>(~_BV(PC2));
    }

    if (g_ledDutyG > phase) {
        out |= _BV(PC3);
    } else {
        out &= static_cast<uint8_t>(~_BV(PC3));
    }

    if (g_ledDutyB > phase) {
        out |= _BV(PC5);
    } else {
        out &= static_cast<uint8_t>(~_BV(PC5));
    }

    PORTC = out;
}

void applyLedSettings(const SettingRGBLedMouse &settings) {
    g_ledSettings = settings;
    g_hasLedSettings = true;

    updateRgbLedOutput();

    const uint8_t r = settings.color >> 16;
    const uint8_t g = settings.color >> 8;
    const uint8_t b = settings.color >> 0;

    Serial.print("LED cfg seq=");
    Serial.print(settings.seq);
    Serial.print(" bright=");
    Serial.print(settings.brightness);
    Serial.print(" gradient=");
    Serial.println(settings.isGradient ? "1" : "0");
    Serial.print(" color=");
    Serial.println(String(r) + "." + String(g) + "." + String(b));

}

bool isValidLedSettingsPacket(const SettingRGBLedMouse &packet) {
    if (packet.magic != LED_SETTINGS_MAGIC || packet.version != LED_SETTINGS_VERSION) {
        return false;
    }
    return packet.checksum == calcLedSettingsChecksum(packet);
}

void saveLedSettingsToEeprom(const SettingRGBLedMouse &settings) {
    PersistedLedSettings stored{};
    stored.marker = EEPROM_LED_MARKER;
    stored.settings = settings;
    stored.markerXor = static_cast<uint8_t>(EEPROM_LED_MARKER ^ 0xFF);
    EEPROM.put(EEPROM_LED_ADDR, stored);
}

bool loadLedSettingsFromEeprom(SettingRGBLedMouse &outSettings) {
    PersistedLedSettings stored{};
    EEPROM.get(EEPROM_LED_ADDR, stored);

    if (stored.marker != EEPROM_LED_MARKER ||
        stored.markerXor != static_cast<uint8_t>(EEPROM_LED_MARKER ^ 0xFF)) {
        return false;
    }

    if (!isValidLedSettingsPacket(stored.settings)) {
        return false;
    }

    outSettings = stored.settings;
    return true;
}

void pollAckPayload() {
    while (radio.isAckPayloadAvailable()) {
        const uint8_t payloadSize = radio.getDynamicPayloadSize();
        if (payloadSize == 0 || payloadSize > 32) {
            uint8_t trash[32];
            radio.read(&trash, sizeof(trash));
            continue;
        }

        if (payloadSize == sizeof(SettingRGBLedMouse)) {
            SettingRGBLedMouse packet{};
            radio.read(&packet, sizeof(packet));

            if (!isValidLedSettingsPacket(packet)) {
                continue;
            }

            if (!g_hasLedSettings || !ledSettingsEquivalent(g_ledSettings, packet)) {
                saveLedSettingsToEeprom(packet);
            }
            applyLedSettings(packet);
            continue;
        }

        uint8_t skip[32];
        radio.read(&skip, payloadSize);
    }
}

uint8_t readEncoderState() {
    const uint8_t a = (digitalRead(PIN_ENC_A) == HIGH) ? 1 : 0;
    const uint8_t b = (digitalRead(PIN_ENC_B) == HIGH) ? 1 : 0;
    return static_cast<uint8_t>((a << 1) | b);
}

void updateEncoderFromPins() {
    static constexpr int8_t QUAD_TABLE[16] = {
        0,  1, -1,  0,
       -1,  0,  0,  1,
        1,  0,  0, -1,
        0, -1,  1,  0
    };

    const uint8_t currentState = readEncoderState();
    if (currentState == encoderPrevState) {
        return;
    }

    const uint8_t transition = static_cast<uint8_t>((encoderPrevState << 2) | currentState);
    const int8_t delta = QUAD_TABLE[transition];
    encoderPrevState = currentState;

    if (delta == 0) {
        return;
    }

    encoderAccumulator += delta;

    if (encoderAccumulator >= ENCODER_STEPS_PER_DETENT) {
        encoderAccumulator -= ENCODER_STEPS_PER_DETENT;
        ++encoderWheelPending;
        return;
    }
    if (encoderAccumulator <= -ENCODER_STEPS_PER_DETENT) {
        encoderAccumulator += ENCODER_STEPS_PER_DETENT;
        --encoderWheelPending;
    }
}

void encoderISR() {
    updateEncoderFromPins();
}

int8_t takeWheelStep() {
    noInterrupts();
    int16_t pending = encoderWheelPending;
    if (pending > 0) {
        --encoderWheelPending;
    } else if (pending < 0) {
        ++encoderWheelPending;
    }
    interrupts();

    if (pending > 0) {
        return 1;
    }
    if (pending < 0) {
        return -1;
    }
    return 0;
}

void calibrateJoystickCenter() {
    int32_t sumX = 0;
    int32_t sumY = 0;
    for (uint8_t i = 0; i < 16; ++i) {
        sumX += analogRead(PIN_JOY_X);
        sumY += analogRead(PIN_JOY_Y);
        delay(2);
    }
    joyCenterX = static_cast<int16_t>(sumX / 16);
    joyCenterY = static_cast<int16_t>(sumY / 16);
}

int8_t axisToMouseDelta(const int16_t value, const int16_t center, const bool invert) {
    int16_t delta = value - center;
    if (invert) {
        delta = -delta;
    }

    const int16_t magnitude = abs(delta);
    if (magnitude <= JOY_DEADZONE) {
        return 0;
    }

    int16_t maxTravel = (delta > 0) ? (1023 - center) : center;
    if (maxTravel <= JOY_DEADZONE) {
        maxTravel = JOY_DEADZONE + 1;
    }

    int16_t scaled = static_cast<int16_t>((static_cast<int32_t>(magnitude - JOY_DEADZONE) * JOY_MAX_SPEED) /
                                          (maxTravel - JOY_DEADZONE));
    if (scaled < 1) {
        scaled = 1;
    }
    if (scaled > JOY_MAX_SPEED) {
        scaled = JOY_MAX_SPEED;
    }

    return (delta > 0) ? static_cast<int8_t>(scaled) : static_cast<int8_t>(-scaled);
}

void readJoystick(int8_t &dx, int8_t &dy) {
    const int16_t x = analogRead(PIN_JOY_X);
    const int16_t y = analogRead(PIN_JOY_Y);

    dx = axisToMouseDelta(x, joyCenterX, JOY_INVERT_X);
    dy = axisToMouseDelta(y, joyCenterY, JOY_INVERT_Y);
}

uint8_t test()
{
    uint8_t buttons = 0;
    if (digitalRead(PIN_RIGHT) == HIGH) {
        buttons |= BTN_RIGHT_MASK;
    }
    if (digitalRead(PIN_MIDDLE) == HIGH)
    {
        buttons |= BTN_MIDDLE_MASK;
    }
    if (digitalRead(PIN_LEFT) == HIGH || digitalRead(PIN_JOY_SW) == LOW)
    {
        buttons |= BTN_LEFT_MASK;
    }
    return buttons;
}

void setup() {
    Serial.begin(9600);
    setupLedPwm();
    pinMode(PIN_RIGHT, INPUT);
    pinMode(PIN_MIDDLE, INPUT);
    pinMode(PIN_LEFT, INPUT);
    pinMode(PIN_JOY_SW, INPUT_PULLUP);
    pinMode(PIN_ENC_A, INPUT_PULLUP);
    pinMode(PIN_ENC_B, INPUT_PULLUP);

    calibrateJoystickCenter();

    encoderPrevState = readEncoderState();
    attachInterrupt(digitalPinToInterrupt(PIN_ENC_A), encoderISR, CHANGE);
    attachInterrupt(digitalPinToInterrupt(PIN_ENC_B), encoderISR, CHANGE);

    radio.begin();
    radio.openWritingPipe(address);
    radio.setPALevel(RF24_PA_LOW);
    radio.setDataRate(RF24_1MBPS);
    radio.enableDynamicPayloads();
    radio.enableAckPayload();
    radio.stopListening();

    SettingRGBLedMouse loaded{};
    if (loadLedSettingsFromEeprom(loaded)) {
        applyLedSettings(loaded);
    } else {
        setLedDuty(0, 0, 0);
    }
}

uint8_t seq = 0;
void loop() {
    updateRgbLedOutput();

    const uint8_t buttons = test();
    const int8_t wheel = takeWheelStep();
    int8_t dx = 0;
    int8_t dy = 0;
    readJoystick(dx, dy);

    MousePacket p{};
    p.magic = 0xA5;
    p.version = 1;
    p.seq = seq++;
    p.dx = dx;
    p.dy = dy;
    p.wheel = wheel;
    p.buttons = buttons;
    p.checksum = p.magic ^ p.version ^ p.seq ^
        static_cast<uint8_t>(p.dx) ^ static_cast<uint8_t>(p.dy) ^
        static_cast<uint8_t>(p.wheel) ^ p.buttons;

    bool ok = radio.write(&p, sizeof(p));
    if (ok) {
        pollAckPayload();
    }

    Serial.print("Sent: ");
    Serial.print(p.magic);
    Serial.print(" | ");
    Serial.print(p.version);
    Serial.print(" | ");
    Serial.print(p.seq);
    Serial.print(" | ");
    Serial.print(p.dx);
    Serial.print(" | ");
    Serial.print(p.dy);
    Serial.print(" | ");
    Serial.print(p.wheel);
    Serial.print(" | ");
    Serial.print(p.buttons, BIN);
    Serial.print(" | ");
    Serial.print(p.checksum);
    Serial.print(" | ");
    Serial.println(ok ? "OK" : "FAIL");
    delay(5);
}