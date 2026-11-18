// ===============================================
// 환경 센서 시리얼 시험 (아두이노 Uno, 실제 Mega 코드 아님)
// MQ-9 원시값 + DHT11 온습도를 5초마다 JSON 한 줄로 출력
//   → PC의 env_bridge_test.py 가 받아서 MQTT sensor/env, event/mega(ENV_ALARM)로 발행
//
// 배선: MQ-9 AO -> A0 (MQ-9 전원은 별도 5V, GND는 아두이노와 공통)
//       DHT11 DATA -> D4 (전원은 아두이노 5V)
//       수동형 부저 모듈 I/O -> D8 (전원은 아두이노 5V)
// 필요 라이브러리: DHT sensor library (Adafruit)
// 시리얼 속도: 9600
//
// 경고 동작
//   - 가스·온도·습도 중 하나라도 기준을 넘으면 부저가 "삐-삐-" 반복, 모두 해제 기준 아래로 내려가면 멈춤
//   - 경고 메시지는 센서별로 기준을 넘는 순간 한 번만 보냄 (해제된 뒤 다시 넘으면 또 보냄)
//   - 가스는 예열(3분)이 끝난 뒤부터 판단. 온습도는 읽기에 성공한 값으로만 판단
//
// 출력 예 (시리얼 키 이름은 시험용 임시. 실제 Mega 키는 하드웨어 담당이 정함)
//   {"t":"env","gas":182,"temp":24.1,"hum":41.0}
//   {"t":"env","gas":185,"temp":null,"hum":null}                           <- DHT11 두 번 다 실패
//   {"t":"alarm","sensor":"gas_1","metric":"GAS","value":612}              <- 기준 넘은 순간 한 번
//   {"t":"alarm","sensor":"temp_hum_1","metric":"TEMPERATURE","value":36.0}
// ===============================================

#include <DHT.h>

// ---------- 수정할 값 ----------
const int   GAS_AO_PIN          = A0;
const int   DHT_PIN             = 4;
#define     DHT_TYPE DHT11
const unsigned long ENV_MS      = 5000;      // 출력 간격 (규약 기본 5초)
const unsigned long WARMUP_MS   = 180000UL;  // MQ-9 예열 3분 (이 동안은 가스 경고 안 함)
const int   GAS_AVG_SAMPLES     = 10;        // 원시값 평균 횟수

// 경고 기준 (모두 임시값. 실제 값은 팀이 정함)
const int   GAS_ALARM_RAW       = 200;       // 가스 경고. 시험용 (평소 값 약 75~120raw보다 충분히 높게. 가짜 Mega 임시값은 400)
const int   GAS_CLEAR_RAW       = 160;       // 이 아래로 내려가야 해제
const float TEMP_ALARM_C        = 35.0;      // 온도 경고
const float TEMP_CLEAR_C        = 33.0;
const float HUM_ALARM_PCT       = 80.0;      // 습도 경고
const float HUM_CLEAR_PCT       = 75.0;

// DHT11 쓰레기 값 거르기
const float DHT_HUM_MIN         = 1.0;       // 이 범위 밖이면 읽기 실패로 봄 (0%는 쓰레기 값)
const float DHT_HUM_MAX         = 100.0;
const float DHT_TEMP_MIN        = 1.0;       // 0.3C 쓰레기 값 거르기 (실내 라인이라 영하 없음)
const float DHT_TEMP_MAX        = 60.0;

// 부저
const int   BUZZER_PIN          = 8;         // 수동형 부저 I/O (3·11번은 tone()과 충돌해서 피함)
const int   BUZZER_IDLE         = HIGH;      // 쉴 때 핀 상태. 모듈 트랜지스터가 8550이면 HIGH, 8050·9013이면 LOW
const int   BUZZER_HZ           = 2000;      // 소리 높이
const unsigned long BEEP_ON_MS  = 400;       // 경고 중 울리는 시간
const unsigned long BEEP_OFF_MS = 400;       // 경고 중 쉬는 시간
const unsigned long BEEP_MAX_ON_MS = 1000;   // 안전장치: 한 번 켜진 소리는 이 시간을 넘기지 않음
// -------------------------------

DHT dht(DHT_PIN, DHT_TYPE);
unsigned long startMs;
unsigned long lastEnvMs = 0;

bool gasAlarm  = false;
bool tempAlarm = false;
bool humAlarm  = false;

bool beepOn = false;
unsigned long beepChangeMs = 0;

// ---------- 부저 ----------
void buzzerOff() {
  noTone(BUZZER_PIN);
  digitalWrite(BUZZER_PIN, BUZZER_IDLE);   // noTone()은 핀을 LOW로 두므로 쉬는 상태로 다시 맞춤
  beepOn = false;
}

// 매 loop마다, 그리고 waitMs()로 기다리는 동안 부름. 경고 중이면 켜고 끄기를 반복
void updateBuzzer(unsigned long now) {
  bool anyAlarm = gasAlarm || tempAlarm || humAlarm;
  if (!anyAlarm) {
    if (beepOn) buzzerOff();
    return;
  }
  // 안전장치: 어떤 이유로든 한 번 켜진 소리가 BEEP_MAX_ON_MS를 넘으면 무조건 끔
  if (beepOn && now - beepChangeMs >= BEEP_MAX_ON_MS) {
    buzzerOff();
    beepChangeMs = now;
    return;
  }
  unsigned long wait = beepOn ? BEEP_ON_MS : BEEP_OFF_MS;
  if (now - beepChangeMs < wait) return;
  beepChangeMs = now;
  if (beepOn) {
    buzzerOff();
  } else {
    tone(BUZZER_PIN, BUZZER_HZ);
    beepOn = true;
  }
}

// delay() 대신 씀: 기다리는 동안에도 부저 켜기/끄기를 계속 처리
// (delay로 멈춰 있으면 "삐-"가 켜진 채로 몇 초씩 이어짐)
void waitMs(unsigned long ms) {
  unsigned long start = millis();
  while (millis() - start < ms) {
    updateBuzzer(millis());
  }
}

// ---------- 센서 ----------
int readGasRaw() {
  long sum = 0;
  for (int i = 0; i < GAS_AVG_SAMPLES; i++) {
    sum += analogRead(GAS_AO_PIN);
    waitMs(5);
  }
  return sum / GAS_AVG_SAMPLES;
}

// DHT11 값이 말이 되는지 확인
// 실패 직후 다시 읽으면 0.3C, 0% 같은 쓰레기 값이 숫자로 나옴 → 실패로 처리
bool dhtValid(float temp, float hum) {
  if (isnan(hum) || isnan(temp)) return false;
  if (hum < DHT_HUM_MIN || hum > DHT_HUM_MAX) return false;
  if (temp < DHT_TEMP_MIN || temp > DHT_TEMP_MAX) return false;
  return true;
}

// DHT11 읽기: 실패하면 1번 다시 읽음. 둘 다 실패하면 false
bool readDht(float &temp, float &hum) {
  for (int attempt = 0; attempt < 2; attempt++) {
    hum  = dht.readHumidity();
    temp = dht.readTemperature();
    if (dhtValid(temp, hum)) return true;
    waitMs(2100);   // DHT11은 2초 이상 간격 필요 (기다리는 동안 부저 박자 유지)
  }
  return false;
}

void printNumberOrNull(float v, bool ok) {
  if (ok) Serial.print(v, 1);
  else    Serial.print(F("null"));
}

void printAlarm(const __FlashStringHelper *sensor, const __FlashStringHelper *metric, float value, int digits) {
  Serial.print(F("{\"t\":\"alarm\",\"sensor\":\""));
  Serial.print(sensor);
  Serial.print(F("\",\"metric\":\""));
  Serial.print(metric);
  Serial.print(F("\",\"value\":"));
  Serial.print(value, digits);
  Serial.println(F("}"));
}

// 기준을 넘는 순간 true로 바꾸고 경고를 보냄. 해제 기준 아래로 내려가면 false
// 돌려주는 값: 지금 경고 중인지
bool checkAlarm(bool on, float value, float alarmAt, float clearAt,
                const __FlashStringHelper *sensor, const __FlashStringHelper *metric, int digits) {
  if (!on && value >= alarmAt) {
    printAlarm(sensor, metric, value, digits);
    return true;
  }
  if (on && value < clearAt) return false;
  return on;
}

void setup() {
  Serial.begin(9600);
  pinMode(BUZZER_PIN, OUTPUT);
  buzzerOff();
  dht.begin();
  delay(2000);
  dht.readTemperature();   // 켜자마자 읽는 첫 값(0.3C, 0%)은 버림
  dht.readHumidity();
  startMs = millis();
  lastEnvMs = startMs - ENV_MS;   // 첫 출력은 바로
}

void loop() {
  unsigned long now = millis();
  updateBuzzer(now);
  if (now - lastEnvMs < ENV_MS) return;
  lastEnvMs = now;

  int gas = readGasRaw();
  float temp = 0, hum = 0;
  bool dhtOk = readDht(temp, hum);

  // 환경 값 한 줄
  Serial.print(F("{\"t\":\"env\",\"gas\":"));
  Serial.print(gas);
  Serial.print(F(",\"temp\":"));
  printNumberOrNull(temp, dhtOk);
  Serial.print(F(",\"hum\":"));
  printNumberOrNull(hum, dhtOk);
  Serial.println(F("}"));

  // 경고 판단
  bool warmedUp = (now - startMs) >= WARMUP_MS;
  if (warmedUp) {
    gasAlarm = checkAlarm(gasAlarm, gas, GAS_ALARM_RAW, GAS_CLEAR_RAW, F("gas_1"), F("GAS"), 0);
  }
  if (dhtOk) {   // 읽기 실패한 회차는 이전 경고 상태 유지
    tempAlarm = checkAlarm(tempAlarm, temp, TEMP_ALARM_C, TEMP_CLEAR_C, F("temp_hum_1"), F("TEMPERATURE"), 1);
    humAlarm  = checkAlarm(humAlarm,  hum,  HUM_ALARM_PCT, HUM_CLEAR_PCT, F("temp_hum_1"), F("HUMIDITY"), 1);
  }
}
