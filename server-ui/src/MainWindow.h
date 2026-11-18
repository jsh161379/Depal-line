// MainWindow.h - 서버 관리 창
//  창 제목: DEPAL 서버 - PC 이름 (IP)
//  도구 모음: 전부 켜기 · 전부 끄기 | 켜기 · 끄기 | 로그 열기 ...... [가짜 장치 포함]
//  가운데 탭: 프로그램(ProgramsPage) · 장치 접속(DevicesPage) · 메시지(MessagesPage) · 남은 기록(RetainedPage)
//  장치 접속·메시지 탭은 MqttLite 하나로 브로커의 모든 토픽(#)을 받아 나눠 씀
//  상태 줄: 서버 상태·가동 시간 | MQTT | MySQL | 알림
#pragma once

#include "RunnerClient.h"

#include <QMainWindow>

class QAction;
class QCheckBox;
class QLabel;
class MqttLite;
class PortProbe;
class ProgramsPage;
class QTabWidget;

class MainWindow : public QMainWindow
{
    Q_OBJECT
public:
    explicit MainWindow(QWidget *parent = nullptr);

private:
    void buildToolBar();
    void buildStatusBar();
    void onStatus(const RunnerStatus &status);
    void updateActions();
    void onMessage(const QString &text, bool warning);
    void startAll();
    void stopAll();
    void bindPort(PortProbe *probe, QLabel *label, const QString &name, quint16 port);
    static QString localIp();

    RunnerClient *m_runner;
    ProgramsPage *m_programs;
    QTabWidget *m_tabs;
    MqttLite *m_mqtt;
    PortProbe *m_mqttProbe;
    PortProbe *m_mysqlProbe;

    QAction *m_startAll;
    QAction *m_stopAll;
    QAction *m_start;
    QAction *m_stop;
    QAction *m_openLog;
    QCheckBox *m_withFake;

    QLabel *m_serverLabel;
    QLabel *m_mqttLabel;
    QLabel *m_mysqlLabel;
    QLabel *m_messageLabel;
};
