// RetainedPage.h - 남은 기록 탭: 브로커가 보관 중인 메시지(retained)를 보고 골라서 지움
//
//  ┌ 버튼 줄: 새로 고침 · 예전 이름 고르기 · 고른 것 풀기 ............ 고른 것 지우기 · 개수
//  └ 표: [고름] 토픽, 구분(예전 이름 / 지금 씀), 받은 시각, 내용(한 줄)
//
//  retained란: 브로커가 토픽마다 마지막 메시지 하나를 보관해 두었다가, 나중에 구독한 쪽에 바로 보내 주는 것.
//  장치 이름이 바뀌면(pi → arm1, ui-PC이름 → admin-번호) 예전 이름의 status/... 기록이 계속 남아
//  화면에 "끊김" 장치로 보인다. 같은 토픽에 "빈 내용 + retain"을 보내면 브로커가 그 기록을 지운다.
//
//  목록 모으는 법: 구독 직후 브로커가 보내는 retained 메시지를 모음. [새로 고침]은 구독을 다시 보내
//  브로커가 처음부터 다시 보내게 한다 (MQTT 3.1.1: 같은 구독을 다시 받으면 retained를 다시 보냄).
#pragma once

#include "MqttLite.h"

#include <QHash>
#include <QSet>
#include <QWidget>

class QLabel;
class QPushButton;
class QTableWidget;
class QTimer;

class RetainedPage : public QWidget
{
    Q_OBJECT
public:
    explicit RetainedPage(MqttLite *mqtt, QWidget *parent = nullptr);

signals:
    void notice(const QString &text, bool warning);

private:
    struct Item
    {
        QByteArray payload;
        QDateTime received;
    };

    void onMessage(const MqttMessage &msg);
    void refresh();
    void rebuild();
    void selectOld();
    void selectNone();
    void deleteChecked();
    QStringList checkedTopics() const;
    void updateButtons();

    static bool isOldName(const QString &topic);

    MqttLite *m_mqtt;
    QTableWidget *m_table;
    QPushButton *m_refresh;
    QPushButton *m_pickOld;
    QPushButton *m_pickNone;
    QPushButton *m_delete;
    QLabel *m_count;
    QTimer *m_rebuildTimer;   // 메시지가 몰려올 때 표를 한 번만 다시 그림

    QHash<QString, Item> m_items;    // 토픽 → 마지막으로 본 보관 기록
    QSet<QString> m_checked;         // 고른 토픽 (표를 다시 그려도 유지)
};
