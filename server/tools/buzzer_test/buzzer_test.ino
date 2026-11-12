// ===============================================
// 수동형 부저 모듈 단독 시험 (아두이노 Uno)
// 배선: 부저 VCC -> 5V(아두이노 쪽 + 레일), GND -> GND, I/O -> D8
// 3초마다 "삐-삐-삐" (짧게 3번) 울림. 시리얼 모니터 9600에 상태 출력
//
// 수동형 부저는 전기만 줘서는 소리가 안 나고, tone()으로 진동(주파수)을 만들어 줘야 함
// ===============================================

// ---------- 수정할 값 ----------
const int  BUZZER_PIN   = 8;      // I/O 연결 핀 (D2=MQ-9 DO, D4=DHT11, 3·11번은 tone()과 충돌해서 피함)
const int  BUZZER_IDLE  = HIGH;   // 쉴 때 핀 상태. 모듈 트랜지스터가 8550이면 HIGH, 8050·9013이면 LOW
const int  BEEP_HZ      = 2000;   // 소리 높이 (수동형 부저는 2000~4000Hz에서 가장 크게 남)
const int  BEEP_MS      = 150;    // 한 번 울리는 시간
const int  GAP_MS       = 100;    // 울림 사이 쉬는 시간
const int  BEEP_COUNT   = 3;      // 한 번에 울리는 횟수
const unsigned long REPEAT_MS = 3000;  // 반복 간격
// -------------------------------

void buzzerOff() {
  noTone(BUZZER_PIN);
  digitalWrite(BUZZER_PIN, BUZZER_IDLE);   // noTone()은 핀을 LOW로 두므로 쉬는 상태로 다시 맞춤
}

void beep(int hz, int ms) {
  tone(BUZZER_PIN, hz);
  delay(ms);
  buzzerOff();
}

void setup() {
  Serial.begin(9600);
  pinMode(BUZZER_PIN, OUTPUT);
  buzzerOff();
  Serial.println(F("Buzzer test start (3 beeps every 3s)"));
}

void loop() {
  Serial.println(F("beep"));
  for (int i = 0; i < BEEP_COUNT; i++) {
    beep(BEEP_HZ, BEEP_MS);
    delay(GAP_MS);
  }
  delay(REPEAT_MS);
}
