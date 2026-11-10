// ProgramsPage.h - 프로그램 목록 + 고른 프로그램의 상세·로그
//
//  ┌ 목록: 이름, 상태, PID, 가동 시간, 종료 코드, 설명
//  ├───── (끌어서 높이 조절)
//  └ 상세: 왼쪽 정보(상태, PID, 시작 시각, 로그 파일) │ 오른쪽 로그 마지막 부분 (1초마다 이어 읽기)
//
//  켜기·끄기·다시 켜기는 메인 창 도구 모음에서 부르고, 이 클래스가 확인 창과 요청을 처리한다.
#pragma once

#include "RunnerClient.h"

#include <QHash>
#include <QWidget>

class QLabel;
class QPlainTextEdit;
class QTableWidget;

class ProgramsPage : public QWidget
{
    Q_OBJECT
public:
    explicit ProgramsPage(RunnerClient *runner, QWidget *parent = nullptr);

    void updateStatus(const RunnerStatus &status);

    QString selectedName() const;
    QString selectedState() const;   // running / stopped / exited, 실행기가 꺼져 있으면 빈 값
    void requestSelected(const QString &action);   // start / stop / restart
    void openSelectedLog();                       // 메모장 등 기본 프로그램으로 전체 로그 열기

signals:
    void selectionChanged();
    void notice(const QString &text, bool warning);

private:
    void rebuildRows(const QList<ProgramInfo> &programs);
    void showDetail();
    void refreshLog(bool reload);
    void appendLogLines(const QString &text);

    static QString label(const QString &name);
    static QString uptime(const QDateTime &started);

    RunnerClient *m_runner;
    QTableWidget *m_table;

    // 상세
    QLabel *m_detailName;
    QLabel *m_detailState;
    QLabel *m_detailPid;
    QLabel *m_detailStarted;
    QLabel *m_detailLog;
    QPlainTextEdit *m_logView;

    // 로그 이어 읽기
    QString m_logPath;
    qint64 m_logPos = 0;
    QByteArray m_logRest;   // 줄 끝(\n)이 아직 안 온 마지막 조각

    QStringList m_names;                       // 지금 표에 있는 프로그램 순서
    QHash<QString, ProgramInfo> m_programs;    // 이름 → 마지막 상태
    bool m_alive = false;
};
