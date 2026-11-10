// RunnerClient.h - run_all.py(실행기)와 파일로 대화하는 클래스
//
//  읽기: logs/run_all.status.json  (실행기가 1초마다 씀)
//  쓰기: logs/run_all.request.json (켜기·끄기·다시 켜기 요청)
//  실행: python run_all.py --hidden / --stop  (창 없이)
//
// 서버 UI는 프로그램을 직접 켜지 않고 실행기에 부탁만 한다.
// 그래서 서버 UI가 꺼지거나 죽어도 서버와 라인은 계속 돈다.
#pragma once

#include <QDateTime>
#include <QJsonObject>
#include <QList>
#include <QObject>
#include <QString>
#include <QTimer>

struct ProgramInfo {
    QString name;        // broker, server, controller, db_writer, fake_pi, fake_yolo
    QString state;       // running(실행 중) / stopped(꺼 둠) / exited(스스로 꺼짐 = 이상)
    qint64 pid = -1;
    QDateTime started;   // 마지막으로 켠 시각
    bool hasExitCode = false;
    int exitCode = 0;
    QString log;         // logs/<이름>.log (서버 폴더 기준)
};

struct RunnerStatus {
    bool fileFound = false;   // 상태 파일이 있는지
    bool running = false;     // 실행기가 켜져 있다고 기록했는지
    bool stale = false;       // 기록이 오래됨 (실행기가 강제로 꺼졌거나 멈춤)
    qint64 pid = -1;
    QDateTime started;
    QDateTime updated;
    bool hidden = false;      // 창 없는 모드
    bool brokerExternal = false;  // 실행기 밖에서 켠 브로커를 쓰는 중
    bool noFake = false, noDb = false, estopTest = false;
    QList<ProgramInfo> programs;

    bool alive() const { return fileFound && running && !stale; }
};

class RunnerClient : public QObject
{
    Q_OBJECT
public:
    explicit RunnerClient(QObject *parent = nullptr);

    QString serverDir() const { return m_serverDir; }
    bool serverDirOk() const { return !m_serverDir.isEmpty(); }
    const RunnerStatus &status() const { return m_status; }

    // MySQL 비밀번호가 환경변수(AA_DB_PASSWORD)에 저장되어 있는지
    static bool hasSavedDbPassword();

    // 실행기 켜기. password가 비어 있고 저장된 비밀번호도 없으면 --no-db로 켬
    bool startRunner(bool withFakeDevices, const QString &password);
    // 실행기 끄기 (python run_all.py --stop)
    void stopRunner();
    // 프로그램 하나 켜기·끄기·다시 켜기 (action: start / stop / restart)
    bool sendRequest(const QString &action, const QString &name);

signals:
    void statusChanged(const RunnerStatus &status);
    void message(const QString &text, bool warning);

private:
    void poll();
    static QString findServerDir();
    QString logsPath(const QString &file) const;

    QString m_serverDir;
    QTimer m_timer;
    RunnerStatus m_status;
    QString m_pendingId;      // 보낸 요청 번호 (결과를 기다리는 중)
    QString m_pendingText;
    qint64 m_pendingSinceMs = 0;
};
