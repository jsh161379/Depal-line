#include "HbChart.h"

#include <QPainter>
#include <QPainterPath>

#include <algorithm>
#include <cmath>

namespace {

// 수정할 값
constexpr int kWindowSec = 120;   // 가로로 보여 줄 시간
constexpr double kMinTopSec = 6;  // 세로 최댓값 (값이 더 크면 늘어남)

const QColor kGrid(0x33, 0x33, 0x37);
const QColor kAxisText(0xA0, 0xA0, 0xA0);
const QColor kLine(0x4F, 0xA3, 0xE3);
const QColor kWarn(0xE2, 0xC0, 0x8D);
const QColor kAlarm(0xF4, 0x87, 0x71);

}  // namespace

HbChart::HbChart(QWidget *parent) : QWidget(parent)
{
    setMinimumHeight(120);
    setAutoFillBackground(true);
    setBackgroundRole(QPalette::Base);
}

void HbChart::setData(const QVector<HbPoint> &points, qint64 lastMs)
{
    m_points = points;
    m_lastMs = lastMs;
    update();
}

void HbChart::setThresholds(double warnSec, double offlineSec)
{
    m_warnSec = warnSec;
    m_offlineSec = offlineSec;
    update();
}

void HbChart::paintEvent(QPaintEvent *)
{
    QPainter p(this);
    p.setRenderHint(QPainter::Antialiasing);
    p.setFont(QFont(font().family(), 8));

    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    const qint64 from = now - kWindowSec * 1000LL;
    const double waiting = m_lastMs > 0 ? (now - m_lastMs) / 1000.0 : 0.0;   // 지금까지 기다린 시간

    // 세로 최댓값: 기본 6초, 더 큰 값이 있으면 2초 단위로 올림
    double top = kMinTopSec;
    for (const auto &pt : m_points)
        if (pt.ms >= from)
            top = std::max(top, pt.interval);
    top = std::max(top, std::min(waiting, 60.0));
    top = std::ceil(top / 2.0) * 2.0;

    const QRectF area(36, 8, width() - 36 - 10, height() - 8 - 22);
    auto xOf = [&](qint64 ms) { return area.left() + area.width() * double(ms - from) / (kWindowSec * 1000.0); };
    auto yOf = [&](double sec) { return area.bottom() - area.height() * std::min(sec, top) / top; };

    // 눈금
    p.setPen(kAxisText);
    const double step = top <= 8 ? 2 : top <= 20 ? 5 : 10;
    for (double s = 0; s <= top + 1e-9; s += step) {
        p.setPen(kGrid);
        p.drawLine(QPointF(area.left(), yOf(s)), QPointF(area.right(), yOf(s)));
        p.setPen(kAxisText);
        p.drawText(QRectF(0, yOf(s) - 8, 32, 16), Qt::AlignRight | Qt::AlignVCenter, QStringLiteral("%1초").arg(s));
    }
    for (int s = kWindowSec; s >= 0; s -= 30) {
        const double x = xOf(now - s * 1000LL);
        const QString text = s == 0 ? QStringLiteral("지금") : QStringLiteral("-%1초").arg(s);
        p.drawText(QRectF(x - 30, area.bottom() + 4, 60, 16), Qt::AlignHCenter | Qt::AlignTop, text);
    }

    // 기준선
    auto dashed = [&](double sec, const QColor &color) {
        if (sec > top)
            return;
        QPen pen(color, 1, Qt::DashLine);
        p.setPen(pen);
        p.drawLine(QPointF(area.left(), yOf(sec)), QPointF(area.right(), yOf(sec)));
    };
    dashed(m_warnSec, kWarn);
    dashed(m_offlineSec, kAlarm);

    if (m_points.isEmpty() && m_lastMs == 0) {
        p.setPen(kAxisText);
        p.drawText(area, Qt::AlignCenter, QStringLiteral("하트비트를 받은 적이 없습니다"));
        return;
    }

    // 간격 선
    QPainterPath path;
    bool started = false;
    const HbPoint *peak = nullptr;
    for (const auto &pt : m_points) {
        if (pt.ms < from)
            continue;
        const QPointF xy(xOf(pt.ms), yOf(pt.interval));
        if (!started) {
            path.moveTo(xy);
            started = true;
        } else {
            path.lineTo(xy);
        }
        if (pt.interval >= m_warnSec && (!peak || pt.interval > peak->interval))
            peak = &pt;
    }
    p.setPen(QPen(kLine, 1.5));
    p.drawPath(path);

    // 아직 다음 하트비트가 안 옴 → 기다린 시간을 점선으로
    if (m_lastMs > 0 && waiting > 1.5) {
        const QColor c = waiting >= m_offlineSec ? kAlarm : waiting >= m_warnSec ? kWarn : kLine;
        p.setPen(QPen(c, 1.5, Qt::DotLine));
        p.drawLine(QPointF(xOf(std::max(m_lastMs, from)), yOf(0)), QPointF(xOf(now), yOf(waiting)));
        p.drawText(QRectF(area.right() - 160, yOf(waiting) - 18, 160, 14), Qt::AlignRight,
                   QStringLiteral("%1초째 안 옴").arg(waiting, 0, 'f', 1));
    }

    // 가장 늦게 온 점 표시
    if (peak) {
        const QPointF xy(xOf(peak->ms), yOf(peak->interval));
        p.setPen(Qt::NoPen);
        p.setBrush(peak->interval >= m_offlineSec ? kAlarm : kWarn);
        p.drawEllipse(xy, 3, 3);
        p.setPen(peak->interval >= m_offlineSec ? kAlarm : kWarn);
        const QString text = QStringLiteral("%1  %2초")
                                 .arg(QDateTime::fromMSecsSinceEpoch(peak->ms).toString(QStringLiteral("HH:mm:ss")))
                                 .arg(peak->interval, 0, 'f', 1);
        const double tx = std::min(xy.x() + 6, area.right() - 110);
        p.drawText(QPointF(tx, std::max(xy.y() - 6, area.top() + 10)), text);
    }
}
