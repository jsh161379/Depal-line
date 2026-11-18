// AGV 본 펌웨어 v6 (agv_main_v5 를 대신함) = 라인트레이싱(agv_stop_mark_v7, 11/13 주행 확인) + 블루투스 명령 합친 것
// v4: 주행은 v7과 똑같음 — 선을 잠깐 놓쳐도 멈추지 않고 계속 감, 1초 넘게 안 보이면 멈추고 실패(line_lost), 후진 찾기 없음
//     속도 5 단위. 정지 표시 멈춤·지나치면 후진은 그대로
// v5: 커브에서 선을 놓치면 마지막에 선이 있던 쪽으로 더 세게 꺾음 (LOST_TURN)
// v6: 선 놓쳐서 멈추는 시간 3초 (라인 공간이 좁아서)
// ==========================================================================================
// 서버 ─MQTT(cmd/agv)─ [server/agv_relay.py] ─블루투스 AGV-01─ [이 코드]
//
// 명령 (통신 규약 v1.4 3-3)
//   go     : 분류 위치(출발 정지 표시)에서 출발 → 다음 정지 표시에서 멈춤 → 팔레트 내림 → done
//   return : 다음 정지 표시(= 분류 위치)까지 가서 멈춤 → done
//   estop  : 즉시 정지 (실행 중이던 go/return은 실패)
//   reset  : 비상정지 해제 (위치를 모르니 stopped. 사람이 분류 위치에 놓고 sethome)
//
// ⚠ 임시 가정 (코스·지게가 정해지면 고칠 곳)
//   - 코스는 한 바퀴 도는 원형: 정지 표시 2개 (분류 위치, 목적지). return도 앞으로 가서 다음 표시까지
//   - 지게 내리기는 아직 없음 → forkDown()에서 FORK_MS만큼 기다리기만 함
//   - 전원 켤 때 AGV는 분류 위치 정지 표시 위에 있다고 봄 (st = home)
//
// 시리얼 모니터(115200)에서 직접 입력도 됨: go / return / estop / reset / sethome / d(센서값 보기)
// 블루투스 이름 AGV-01. "Sketch too big" → 도구 → Partition Scheme → Huge APP
#include <Arduino.h>
#include "BluetoothSerial.h"

// ── 모터 (11/10 확정) ──
constexpr int MOTOR_RX = 16;
constexpr int MOTOR_TX = 17;
constexpr uint8_t FORWARD_DIRECTION[] = {1, 1, 2, 2};   // 앞으로: M1·M2 = 1, M3·M4 = 2
constexpr uint8_t LEFT_MOTORS[] = {3, 4};
constexpr uint8_t RIGHT_MOTORS[] = {1, 2};

// ── 라인 센서 (agv_stop_mark_v7 그대로) ──
struct LineSensor { int pin; unsigned long thresholdUs; };
constexpr LineSensor SENSORS[] = {
  {13, 500},  // S03
  {14, 500},  // S05
  {12, 500},  // S07
  {25, 500},  // S09  ⚠ 11/12 항상 5000 (선·핀 확인 필요)
  {26, 450},  // S11
  {27, 450},  // S13
};
constexpr int SENSOR_COUNT = sizeof(SENSORS) / sizeof(SENSORS[0]);
constexpr bool S03_IS_LEFT = false;
constexpr unsigned long TIMEOUT_US = 5000;
constexpr unsigned long CONTROL_INTERVAL_MS = 30;

constexpr int BASE_SPEED = 70;
constexpr int MAX_SPEED = 100;
constexpr float KP = 8.0f;

// ── 정지 표시·후진 (v6 그대로) ──
constexpr int MARK_MIN_BLACK = 5;
constexpr int MARK_CONFIRM = 2;
constexpr int NORMAL_LINE_MAX = 3;
constexpr int BACK_SPEED = 45;
constexpr unsigned long SETTLE_MS = 400;
constexpr unsigned long BACK_LIMIT_MS = 2500;
constexpr unsigned long LOST_STOP_MS = 3000;   // 선이 이만큼 계속 안 보이면 멈춤
constexpr int LOST_TURN = 45;                  // v5: 선 놓쳤을 때 꺾는 세기 (클수록 세게. 너무 돌면 줄이기)
constexpr int SPEED_STEP = 5;                  // v7: 속도를 5 단위로
constexpr unsigned long LEAVE_MAX_MS = 1500;    // 출발할 때 지금 밟고 있는 정지 표시를 곧게 빠져나가는 최대 시간

// ── 통신·동작 ──
const char* BT_NAME = "AGV-01";
constexpr unsigned long HB_MS = 1000;
constexpr unsigned long LINK_TIMEOUT_MS = 3000;  // 중계에서 3초간 아무것도 못 받으면 움직이다가도 멈춤
constexpr unsigned long FORK_MS = 1500;          // 임시: 지게 내리는 시간 (지게 붙으면 forkDown 고치기)

BluetoothSerial BT;

// ───────── 상태 ─────────
// 작업 상태 (hb의 st)
enum Task { T_HOME, T_GOING, T_AWAY, T_RETURNING, T_STOPPED, T_ESTOP };
const char* taskName(int t) {   // int: 아두이노 IDE 자동 선언 오류 방지
  switch (t) {
    case T_HOME: return "home"; case T_GOING: return "going"; case T_AWAY: return "away";
    case T_RETURNING: return "returning"; case T_STOPPED: return "stopped"; default: return "estop";
  }
}
Task task = T_HOME;
bool loaded = false;        // 팔레트를 실었는지
long curId = 0;             // 실행 중인 go/return의 id (0 = 없음, -1 = 시리얼 모니터에서 직접)
bool unloading = false;     // go 도착 후 지게 내리는 중
unsigned long unloadAt = 0;

// 주행 상태 (다음 정지 표시까지 가기)
enum Drive { D_IDLE, D_LEAVE, D_FOLLOW, D_SETTLE_MARK, D_BACK_MARK };
Drive drv = D_IDLE;
unsigned long drvAt = 0;
int markSeen = 0;
int lastCorrection = 0;            // v7: 마지막으로 꺾던 값
unsigned long lostAt = 0;          // v7: 선이 안 보이기 시작한 때 (0 = 보이는 중)
bool sawNormalLine = false;

bool debugPrint = false;
unsigned long lastLink = 0, lastHb = 0;
String bufBT, bufUSB;

// ───────── 출력 ─────────
void sendBT(const String& s) { BT.println(s); Serial.println("→ " + s); }

void ack(long id, bool ok, const char* err = nullptr) {
  if (id < 0) { Serial.printf("(직접) %s %s\n", ok ? "받음" : "거절", err ? err : ""); return; }
  String s = "{\"t\":\"ack\",\"id\":" + String(id) + ",\"ok\":" + (ok ? "1" : "0");
  if (!ok && err) s += ",\"err\":\"" + String(err) + "\"";
  sendBT(s + "}");
}

void doneMsg(long id, bool ok, const char* err = nullptr) {
  if (id < 0) { Serial.printf("(직접) %s %s\n", ok ? "끝" : "실패", err ? err : ""); return; }
  String s = "{\"t\":\"done\",\"id\":" + String(id) + ",\"ok\":" + (ok ? "1" : "0");
  if (!ok && err) s += ",\"err\":\"" + String(err) + "\"";
  sendBT(s + "}");
}

// ───────── 모터 ─────────
void sendMotor(uint8_t motor, uint8_t direction, uint8_t speed) {
  const uint8_t packet[] = {motor, direction, speed, '\r', '\n'};
  Serial2.write(packet, sizeof(packet));
}

// 속도가 음수면 그 쪽 바퀴는 후진. 9600bps라 바뀌었거나 0.2초 지났을 때만 보냄
void drive(int leftSpeed, int rightSpeed) {
  leftSpeed = constrain(leftSpeed, -MAX_SPEED, MAX_SPEED);
  rightSpeed = constrain(rightSpeed, -MAX_SPEED, MAX_SPEED);
  leftSpeed = (leftSpeed / SPEED_STEP) * SPEED_STEP;     // v7
  rightSpeed = (rightSpeed / SPEED_STEP) * SPEED_STEP;
  static int prevLeft = 999, prevRight = 999;
  static unsigned long sentAt = 0;
  if (leftSpeed == prevLeft && rightSpeed == prevRight && millis() - sentAt < 200) return;
  prevLeft = leftSpeed; prevRight = rightSpeed; sentAt = millis();
  for (uint8_t motor : LEFT_MOTORS) {
    uint8_t dir = FORWARD_DIRECTION[motor - 1];
    if (leftSpeed < 0) dir = (dir == 1) ? 2 : 1;
    sendMotor(motor, dir, abs(leftSpeed));
  }
  for (uint8_t motor : RIGHT_MOTORS) {
    uint8_t dir = FORWARD_DIRECTION[motor - 1];
    if (rightSpeed < 0) dir = (dir == 1) ? 2 : 1;
    sendMotor(motor, dir, abs(rightSpeed));
  }
  Serial2.flush();
}

// ───────── 센서 ─────────
void readSensors(unsigned long elapsed[]) {
  unsigned long releasedAt[SENSOR_COUNT];
  bool pending[SENSOR_COUNT];
  int remaining = SENSOR_COUNT;
  for (const LineSensor& sensor : SENSORS) { digitalWrite(sensor.pin, HIGH); pinMode(sensor.pin, OUTPUT); }
  delayMicroseconds(10);
  for (int i = 0; i < SENSOR_COUNT; i++) { pinMode(SENSORS[i].pin, INPUT); releasedAt[i] = micros(); pending[i] = true; }
  while (remaining > 0) {
    for (int i = 0; i < SENSOR_COUNT; i++) {
      if (!pending[i]) continue;
      const unsigned long duration = micros() - releasedAt[i];
      if (duration >= TIMEOUT_US) elapsed[i] = TIMEOUT_US;
      else if (digitalRead(SENSORS[i].pin) == LOW) elapsed[i] = duration;
      else continue;
      pending[i] = false;
      remaining--;
    }
  }
}

// ───────── 작업 ─────────
void forkDown() {
  // TODO(지게): 지게를 내리는 코드. 지금은 FORK_MS 동안 기다리기만 함
  Serial.println("  (임시) 지게 내리는 중…");
}

void startDrive(bool onMarkNow) {
  drv = onMarkNow ? D_LEAVE : D_FOLLOW;
  drvAt = millis();
  markSeen = 0;
  lastCorrection = 0;
  lostAt = 0;
  sawNormalLine = false;
}

void stopAll() { drv = D_IDLE; unloading = false; drive(0, 0); }

// 주행이 끝났을 때 (다음 정지 표시에 도착 / 실패)
void arrived(bool ok, const char* err) {
  drv = D_IDLE;
  drive(0, 0);
  if (!ok) {
    task = T_STOPPED;
    doneMsg(curId, false, err);
    curId = 0;
    return;
  }
  if (task == T_GOING) {                       // 목적지 도착 → 지게 내림 → 끝
    forkDown();
    unloading = true;
    unloadAt = millis();
  } else if (task == T_RETURNING) {            // 분류 위치 도착 → 끝
    task = T_HOME;
    doneMsg(curId, true);
    curId = 0;
  }
}

void failCurrent(const char* err) {
  if (curId == 0) return;
  stopAll();
  doneMsg(curId, false, err);
  curId = 0;
}

int lastBlackCount = 0;   // 출발할 때 정지 표시 위인지 판단용

void command(const String& c, long id, long count) {
  if (c == "estop") {
    failCurrent("estop");
    stopAll();
    task = T_ESTOP;
    ack(id, true); doneMsg(id, true);
    return;
  }
  if (c == "reset") {
    if (task == T_ESTOP) task = T_STOPPED;
    ack(id, true); doneMsg(id, true);
    return;
  }
  if (curId != 0) { ack(id, false, "busy"); return; }
  if (task == T_ESTOP) { ack(id, false, "estop"); return; }

  if (c == "go") {
    if (task != T_HOME) { ack(id, false, "not_home"); return; }
    curId = id; task = T_GOING; loaded = true;
    Serial.printf("▶ go: A %ld개 싣고 출발\n", count);
    ack(id, true);
    startDrive(lastBlackCount >= MARK_MIN_BLACK);
    return;
  }
  if (c == "return") {
    if (task != T_AWAY && task != T_STOPPED) { ack(id, false, "not_away"); return; }
    curId = id; task = T_RETURNING;
    Serial.println("▶ return: 분류 위치로");
    ack(id, true);
    startDrive(lastBlackCount >= MARK_MIN_BLACK);
    return;
  }
  ack(id, false, "unknown");
}

// ───────── 주행 제어 (30ms마다) ─────────
void control() {
  unsigned long elapsed[SENSOR_COUNT];
  readSensors(elapsed);
  int positionSum = 0, blackCount = 0;
  for (int i = 0; i < SENSOR_COUNT; i++) {
    if (elapsed[i] <= SENSORS[i].thresholdUs || elapsed[i] >= TIMEOUT_US) continue;
    const int position = 2 * i - (SENSOR_COUNT - 1);
    positionSum += S03_IS_LEFT ? position : -position;
    blackCount++;
  }
  lastBlackCount = blackCount;
  const bool onMark = blackCount >= MARK_MIN_BLACK;
  const unsigned long now = millis();
  if (blackCount > 0 && blackCount <= NORMAL_LINE_MAX) sawNormalLine = true;

  if (debugPrint) {
    static unsigned long printedAt = 0;
    if (now - printedAt >= 300) {
      printedAt = now;
      for (int i = 0; i < SENSOR_COUNT; i++)
        Serial.printf("%5lu%s ", elapsed[i], (elapsed[i] > SENSORS[i].thresholdUs && elapsed[i] < TIMEOUT_US) ? "*" : " ");
      Serial.printf("  검은 센서 %d개  [%s]\n", blackCount, taskName(task));
    }
  }

  switch (drv) {
    case D_IDLE:
      drive(0, 0);
      return;

    case D_LEAVE:      // 지금 밟고 있는 정지 표시를 곧게 빠져나감
      if (!onMark || now - drvAt >= LEAVE_MAX_MS) { drv = D_FOLLOW; drvAt = now; }
      else drive(BASE_SPEED, BASE_SPEED);
      return;

    case D_FOLLOW:
      markSeen = (onMark && sawNormalLine) ? markSeen + 1 : 0;
      if (markSeen >= MARK_CONFIRM) {
        drive(0, 0); drv = D_SETTLE_MARK; drvAt = now;
        Serial.println("■ 정지 표시 발견 → 멈춤");
        return;
      }
      if (blackCount == 0) {   // v7: 멈추지 않고 하던 대로 계속
        if (lostAt == 0) lostAt = now;
        if (now - lostAt >= LOST_STOP_MS) {
          Serial.println("✗ 선이 3초 넘게 안 보임 → 멈춤");
          arrived(false, "line_lost");
          return;
        }
        // v5: 마지막에 선이 있던 쪽으로 세게 꺾기 (커브에서 선이 센서 밖으로 나간 경우)
        if (lastCorrection > 0) drive(BASE_SPEED + LOST_TURN, BASE_SPEED - LOST_TURN);
        else if (lastCorrection < 0) drive(BASE_SPEED - LOST_TURN, BASE_SPEED + LOST_TURN);
        else drive(BASE_SPEED, BASE_SPEED);
        return;
      }
      lostAt = 0;
      {
        const float error = static_cast<float>(positionSum) / blackCount;
        const int correction = static_cast<int>(KP * error);
        lastCorrection = correction;
        drive(BASE_SPEED + correction, BASE_SPEED - correction);
      }
      return;

    case D_SETTLE_MARK:
      drive(0, 0);
      if (now - drvAt < SETTLE_MS) return;
      if (onMark) arrived(true, nullptr);
      else { drv = D_BACK_MARK; drvAt = now; Serial.println("  정지 표시를 지나침 → 후진"); }
      return;

    case D_BACK_MARK:
      if (onMark) { Serial.println("✓ 후진해서 정지 표시 위"); arrived(true, nullptr); }
      else if (now - drvAt >= BACK_LIMIT_MS) arrived(false, "mark_lost");
      else drive(-BACK_SPEED, -BACK_SPEED);
      return;

  }
}

// ───────── 받은 줄 처리 ─────────
String getStr(const String& j, const char* key) {
  String k = String("\"") + key + "\":\"";
  int p = j.indexOf(k);
  if (p < 0) return "";
  p += k.length();
  int e = j.indexOf('"', p);
  return e < 0 ? "" : j.substring(p, e);
}

long getNum(const String& j, const char* key) {
  String k = String("\"") + key + "\":";
  int p = j.indexOf(k);
  return p < 0 ? 0 : j.substring(p + k.length()).toInt();
}

void handleLine(String line, bool fromBT) {
  line.trim();
  if (!line.length()) return;
  if (fromBT) lastLink = millis();
  if (line.startsWith("{")) {
    String c = getStr(line, "c");
    if (c == "hb") return;
    command(c, getNum(line, "id"), getNum(line, "count"));
    return;
  }
  // 시리얼 모니터에서 사람이 직접
  if (line == "d") { debugPrint = !debugPrint; Serial.println(debugPrint ? "센서값 출력 켬" : "센서값 출력 끔"); return; }
  if (line == "sethome") { if (curId == 0) { task = T_HOME; Serial.println("ok sethome (분류 위치)"); } return; }
  if (line == "go" || line == "return" || line == "estop" || line == "reset") { command(line, -1, 0); return; }
  Serial.println("모르는 입력: " + line + "  (go / return / estop / reset / sethome / d)");
}

void readLines(Stream& in, String& buf, bool fromBT) {
  while (in.available()) {
    char ch = in.read();
    if (ch == '\n' || ch == '\r') { handleLine(buf, fromBT); buf = ""; }
    else if (buf.length() < 120) buf += ch;
  }
}

// ───────── setup / loop ─────────
void setup() {
  Serial.begin(115200);
  Serial2.begin(9600, SERIAL_8N1, MOTOR_RX, MOTOR_TX);
  for (const LineSensor& sensor : SENSORS) pinMode(sensor.pin, INPUT);
  drive(0, 0);
  BT.begin(BT_NAME);
  Serial.printf("\nAGV 본 펌웨어 v1 (블루투스 %s). 분류 위치에 있다고 보고 대기\n", BT_NAME);
  Serial.println("시리얼 모니터: go / return / estop / reset / sethome / d");
}

void loop() {
  readLines(BT, bufBT, true);
  readLines(Serial, bufUSB, false);
  const unsigned long now = millis();

  // 하트비트
  if (now - lastHb >= HB_MS) {
    lastHb = now;
    BT.println("{\"t\":\"hb\",\"st\":\"" + String(taskName(task)) + "\",\"load\":" + (loaded ? "1" : "0") + "}");
  }

  // 중계가 끊기면 움직이던 동작은 멈춤 (시리얼 모니터에서 직접 시킨 동작은 제외)
  if (curId > 0 && now - lastLink > LINK_TIMEOUT_MS) {
    Serial.println("✗ 중계 연결 끊김 → 정지");
    task = T_STOPPED;
    failCurrent("link_lost");
  }

  // go 도착 후 지게 내리기 끝
  if (unloading && now - unloadAt >= FORK_MS) {
    unloading = false;
    loaded = false;
    task = T_AWAY;
    Serial.println("✓ 목적지 도착, 내림 완료");
    doneMsg(curId, true);
    curId = 0;
  }

  static unsigned long lastControlAt = 0;
  if (now - lastControlAt >= CONTROL_INTERVAL_MS) {
    lastControlAt = now;
    control();
  }
}
