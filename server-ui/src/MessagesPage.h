// MessagesPage.h - 메시지 탭: 브로커에 오가는 메시지를 실시간으로 보여 줌
//
//  ┌ 보기 줄: 종류별 체크(명령·응답 / 알림 / 결과·이벤트 / 라인 상태 / 하트비트 / 센서), 찾기, 멈춤, 지우기
//  ├ 표: 시각, 토픽, 내용(한 줄)          ← 새 메시지는 아래에 붙음 (맨 아래를 보고 있을 때만 따라 내려감)
//  ├───── (끌어서 높이 조절)
//  └ 고른 메시지: 토픽·시각·크기·QoS + JSON을 보기 좋게 펼침
//
//  멈춤 중에도 메시지는 계속 기억하고, 풀면 한꺼번에 표에 다시 채운다.
#pragma once

#include "MqttLite.h"

#include <QWidget>

#include <deque>

class QCheckBox;
class QLabel;
class QLineEdit;
class QPlainTextEdit;
class QPushButton;
class QTableWidget;

class MessagesPage : public QWidget
{
    Q_OBJECT
public:
    explicit MessagesPage(MqttLite *mqtt, QWidget *parent = nullptr);

private:
    enum Kind { KindCommand, KindAlert, KindResult, KindLine, KindHeartbeat, KindSensor, KindOther, KindCount };

    struct Entry
    {
        qint64 id;          // 받은 순서 번호 (지워도 계속 늘어남)
        MqttMessage msg;
        Kind kind;
        QString oneLine;    // 표에 보일 한 줄
    };

    void onMessage(const MqttMessage &msg);
    bool visible(const Entry &e) const;
    void appendRow(const Entry &e);
    void rebuild();
    void showDetail();
    void updateCount();
    const Entry *entry(qint64 id) const;

    static Kind kindOf(const QString &topic);
    static QColor colorOf(Kind kind, const QString &topic);

    QCheckBox *m_kinds[KindCount] = {};
    QLineEdit *m_search;
    QPushButton *m_pause;
    QLabel *m_count;
    QTableWidget *m_table;
    QLabel *m_detailHead;
    QPlainTextEdit *m_detail;

    std::deque<Entry> m_entries;   // 받은 메시지 (최대 AppConfig::kMaxMessages)
    QVector<qint64> m_rowIds;      // 표의 줄 → Entry id
    qint64 m_nextId = 0;
};
