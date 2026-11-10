// AppConfig.h - 수정할 값을 한곳에 모아 둔 파일
#pragma once

#include <QString>

namespace AppConfig {

// 서버 폴더 (run_all.py가 있는 곳). 없으면 실행 파일 위쪽 폴더에서 server\run_all.py를 찾는다
inline const QString kServerDir = QStringLiteral("C:/depal/server");

// run_all.py를 실행할 파이썬 (PATH에 있는 python)
inline const QString kPython = QStringLiteral("python");

// 이 PC에서 확인할 포트 (제목 줄 표시용)
inline const QString kBrokerHost = QStringLiteral("127.0.0.1");
constexpr quint16 kBrokerPort = 1883;
constexpr quint16 kMySqlPort = 3306;
constexpr int kProbeMs = 2000;

// 상태 파일을 읽는 간격, 실행기가 살아 있다고 보는 기준
constexpr int kPollMs = 1000;
constexpr double kRunnerStaleSec = 5.0;   // 상태 파일이 이 시간보다 오래되면 "실행기 응답 없음"
constexpr int kRequestTimeoutMs = 10000;  // 요청 파일을 실행기가 이 시간 안에 안 가져가면 실패로 봄

// 장치 접속·메시지 탭: 브로커에 붙는 이름, 하트비트 기준 (central_server.py의 WARN_AFTER_SEC·OFFLINE_AFTER_SEC와 같게)
inline const QString kMqttClientPrefix = QStringLiteral("server-ui-");   // 뒤에 PC 이름이 붙음
constexpr double kHbWarnSec = 3.0;
constexpr double kHbOfflineSec = 5.0;
constexpr int kMaxMessages = 5000;        // 메시지 탭이 기억하는 최대 개수 (넘으면 오래된 것부터 버림)

}  // namespace AppConfig
