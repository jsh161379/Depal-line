// HbChart.h - 하트비트 간격 그래프 (QPainter로 직접 그림, Qt Charts 필요 없음)
//  가로: 최근 kWindowSec초 (오른쪽 끝 = 지금), 세로: 앞 하트비트와의 간격(초)
//  점선: 지연 기준(노랑), 끊김 기준(빨강). 지연 기준을 넘은 점 중 가장 큰 것에 시각·값 표시.
//  마지막 하트비트 뒤로 아직 다음 것이 안 왔으면, 지금까지 기다린 시간을 점선으로 이어 그림.
#pragma once

#include <QDateTime>
#include <QVector>
#include <QWidget>

struct HbPoint
{
    qint64 ms;        // 받은 시각 (QDateTime::currentMSecsSinceEpoch)
    double interval;  // 앞 하트비트와의 간격(초)
};

class HbChart : public QWidget
{
    Q_OBJECT
public:
    explicit HbChart(QWidget *parent = nullptr);

    // points: 오래된 것부터. lastMs: 마지막 하트비트 시각 (0이면 받은 적 없음)
    void setData(const QVector<HbPoint> &points, qint64 lastMs);
    void setThresholds(double warnSec, double offlineSec);

protected:
    void paintEvent(QPaintEvent *event) override;

private:
    QVector<HbPoint> m_points;
    qint64 m_lastMs = 0;
    double m_warnSec = 3.0;
    double m_offlineSec = 5.0;
};
