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

}  // namespace AppConfig
