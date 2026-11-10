// DevicesPage.h - 장치 접속 탭
//
//  ┌ 표: 장치, 상태, 마지막 수신, 평균 간격, 최대 간격, 놓친 수, 접속한 시각, 추가 정보
//  ├───── (끌어서 높이 조절)
//  └ 고른 장치의 하트비트 간격 그래프 (최근 2분)
//
//  상태(정상/지연/끊김)는 중앙 서버가 판단한 server/devices 값을 그대로 쓴다 (판단 기준이 한곳에만 있게).
//  간격·놓친 수·그래프는 hb/<장치>를 직접 받아 이 화면이 계산한다.
#pragma once

#include "HbChart.h"
#include "MqttLite.h"

#include <QHash>
#include <QJsonObject>
#include <QWidget>

class QLabel;
class QTableWidget;
class QTimer;

class DevicesPage : public QWidget
{
    Q_OBJECT
public:
    explicit DevicesPage(MqttLite *mqtt, QWidget *parent = nullptr);

private:
    struct Device
    {
        QString status = QStringLiteral("unknown");   // server/devices 값: unknown / ok / warn / offline
        bool fromServer = false;                      // 중앙 서버 목록에 있는 장치인가 (없으면 이 화면이 직접 판단)
        qint64 lastMs = 0;                            // 마지막 하트비트 받은 시각
        qint64 connectedMs = 0;                       // 끊겼다가 다시 오기 시작한 시각
        qint64 lastSeq = -1;
        int missed = 0;                               // seq가 건너뛴 개수 (재시작으로 작아지면 다시 셈)
        QVector<HbPoint> points;                      // 최근 간격 기록
        QJsonObject extra;                            // 하트비트의 추가 정보 (arm, serial, belt ...)
    };

    void onMessage(const MqttMessage &msg);
    void onHeartbeat(const QString &name, const QJsonObject &hb, qint64 ms);
    void onSnapshot(const QJsonObject &snap);
    Device &device(const QString &name);
    void refresh();
    void showChart();
    QString selectedName() const;

    static QString statusText(const QString &status);
    static QString extraText(const QJsonObject &extra);

    QTableWidget *m_table;
    QLabel *m_chartTitle;
    HbChart *m_chart;
    QTimer *m_timer;

    QStringList m_order;               // 표 순서 (처음 나타난 순서)
    QHash<QString, Device> m_devices;
};
