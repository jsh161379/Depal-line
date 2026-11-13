#include "DevicesPage.h"

#include "AppConfig.h"

#include <QHBoxLayout>
#include <QHeaderView>
#include <QItemSelectionModel>
#include <QJsonArray>
#include <QJsonDocument>
#include <QLabel>
#include <QSet>
#include <QSplitter>
#include <QTableWidget>
#include <QTimer>
#include <QVBoxLayout>

#include <algorithm>

namespace {

// 수정할 값
constexpr int kRefreshMs = 500;          // 표·그래프 다시 그리는 간격
constexpr qint64 kKeepMs = 130 * 1000;   // 간격 기록을 남길 시간 (그래프 2분 + 여유)
constexpr double kReconnectGapSec = 5.0; // 이보다 오래 안 오다가 다시 오면 "다시 접속"으로 봄

enum Col { ColName, ColState, ColLast, ColAvg, ColMax, ColMissed, ColSince, ColExtra, ColCount };

const QColor kOk(0x6C, 0xCB, 0x5F);
const QColor kWarn(0xE2, 0xC0, 0x8D);
const QColor kAlarm(0xF4, 0x87, 0x71);
const QColor kDim(0xA0, 0xA0, 0xA0);

// 하트비트 공통 칸 (추가 정보에서 뺌)
const QStringList kCommonKeys = {QStringLiteral("device"), QStringLiteral("seq"), QStringLiteral("ts"),
                                 QStringLiteral("state")};

}  // namespace

DevicesPage::DevicesPage(MqttLite *mqtt, QWidget *parent)
    : QWidget(parent), m_timer(new QTimer(this))
{
    m_table = new QTableWidget(0, ColCount, this);
    m_table->setHorizontalHeaderLabels({QStringLiteral("장치"), QStringLiteral("상태"), QStringLiteral("마지막 수신"),
                                        QStringLiteral("평균 간격"), QStringLiteral("최대 간격"), QStringLiteral("놓친 수"),
                                        QStringLiteral("접속한 시각"), QStringLiteral("추가 정보")});
    m_table->verticalHeader()->setVisible(false);
    m_table->verticalHeader()->setDefaultSectionSize(22);
    m_table->setSelectionBehavior(QAbstractItemView::SelectRows);
    m_table->setSelectionMode(QAbstractItemView::SingleSelection);
    m_table->setEditTriggers(QAbstractItemView::NoEditTriggers);
    m_table->setWordWrap(false);
    m_table->setShowGrid(false);
    auto *header = m_table->horizontalHeader();
    header->setDefaultAlignment(Qt::AlignLeft | Qt::AlignVCenter);
    header->setHighlightSections(false);
    header->setStretchLastSection(true);
    m_table->setColumnWidth(ColName, 140);
    m_table->setColumnWidth(ColState, 64);
    m_table->setColumnWidth(ColLast, 96);
    m_table->setColumnWidth(ColAvg, 76);
    m_table->setColumnWidth(ColMax, 76);
    m_table->setColumnWidth(ColMissed, 60);
    m_table->setColumnWidth(ColSince, 84);
    m_table->setToolTip(QStringLiteral("상태는 중앙 서버 판단 (지연 %1초, 끊김 %2초). 놓친 수 = 하트비트 번호(seq)가 건너뛴 개수")
                            .arg(AppConfig::kHbWarnSec).arg(AppConfig::kHbOfflineSec));
    connect(m_table, &QTableWidget::itemSelectionChanged, this, &DevicesPage::showChart);

    // 그래프 머리 줄
    auto *head = new QWidget(this);
    head->setObjectName(QStringLiteral("chartHead"));
    m_chartTitle = new QLabel(head);
    auto *legend = new QLabel(QStringLiteral("<span style='color:#E2C08D'>— %1초 지연</span>&nbsp;&nbsp;"
                                             "<span style='color:#F48771'>— %2초 끊김</span>")
                                  .arg(AppConfig::kHbWarnSec).arg(AppConfig::kHbOfflineSec), head);
    auto *hl = new QHBoxLayout(head);
    hl->setContentsMargins(8, 3, 8, 3);
    hl->addWidget(m_chartTitle);
    hl->addStretch(1);
    hl->addWidget(legend);

    m_chart = new HbChart(this);
    m_chart->setThresholds(AppConfig::kHbWarnSec, AppConfig::kHbOfflineSec);

    auto *bottom = new QWidget(this);
    auto *bl = new QVBoxLayout(bottom);
    bl->setContentsMargins(0, 0, 0, 0);
    bl->setSpacing(0);
    bl->addWidget(head);
    bl->addWidget(m_chart, 1);

    auto *split = new QSplitter(Qt::Vertical, this);
    split->addWidget(m_table);
    split->addWidget(bottom);
    split->setChildrenCollapsible(false);
    split->setStretchFactor(0, 1);
    split->setStretchFactor(1, 1);
    split->setSizes({200, 240});

    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->addWidget(split);

    connect(mqtt, &MqttLite::messageReceived, this, &DevicesPage::onMessage);
    connect(m_timer, &QTimer::timeout, this, &DevicesPage::refresh);
    m_timer->start(kRefreshMs);
    showChart();
}

void DevicesPage::onMessage(const MqttMessage &msg)
{
    const bool hb = msg.topic.startsWith(QStringLiteral("hb/"));
    if (!hb && msg.topic != QStringLiteral("server/devices"))
        return;
    const auto doc = QJsonDocument::fromJson(msg.payload);
    if (!doc.isObject())
        return;
    if (hb)
        onHeartbeat(msg.topic.mid(3), doc.object(), msg.received.toMSecsSinceEpoch());
    else
        onSnapshot(doc.object());
}

DevicesPage::Device &DevicesPage::device(const QString &name)
{
    if (!m_devices.contains(name))
        m_order.append(name);
    return m_devices[name];
}

void DevicesPage::onHeartbeat(const QString &name, const QJsonObject &hb, qint64 ms)
{
    Device &d = device(name);
    if (d.lastMs > 0) {
        const double gap = (ms - d.lastMs) / 1000.0;
        d.points.append({ms, gap});
        if (gap >= kReconnectGapSec)
            d.connectedMs = ms;
    } else {
        d.connectedMs = ms;
    }
    // 오래된 기록 버림
    const qint64 cut = ms - kKeepMs;
    auto it = std::find_if(d.points.begin(), d.points.end(), [cut](const HbPoint &p) { return p.ms >= cut; });
    d.points.erase(d.points.begin(), it);

    const qint64 seq = hb.value(QStringLiteral("seq")).toInteger(-1);
    if (seq >= 0) {
        if (d.lastSeq >= 0 && seq > d.lastSeq + 1)
            d.missed += int(seq - d.lastSeq - 1);
        else if (d.lastSeq >= 0 && seq <= d.lastSeq)
            d.missed = 0;                       // 장치가 다시 켜짐 (seq가 1부터 다시)
        d.lastSeq = seq;
    }
    d.lastMs = ms;

    QJsonObject extra = hb;
    for (const auto &k : kCommonKeys)
        extra.remove(k);
    d.extra = extra;
}

void DevicesPage::onSnapshot(const QJsonObject &snap)
{
    QSet<QString> listed;
    for (const auto &v : snap.value(QStringLiteral("devices")).toArray()) {
        const auto o = v.toObject();
        const QString name = o.value(QStringLiteral("name")).toString();
        if (name.isEmpty())
            continue;
        listed.insert(name);
        Device &d = device(name);
        d.status = o.value(QStringLiteral("status")).toString(QStringLiteral("unknown"));
        d.fromServer = true;
    }
    // 중앙 서버 목록에서 빠진 장치 (서버를 다시 켜서 예전 이름이 사라진 경우 등)
    //  → 하트비트도 최근에 없으면 표에서 뺌. 하트비트가 오는 중이면(hb/server 등) 남기고 직접 판단
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    for (int i = int(m_order.size()) - 1; i >= 0; --i) {
        const QString name = m_order[i];
        Device &d = m_devices[name];
        if (!d.fromServer || listed.contains(name))
            continue;
        d.fromServer = false;
        if (d.lastMs == 0 || now - d.lastMs > qint64(AppConfig::kHbOfflineSec * 1000)) {
            m_devices.remove(name);
            m_order.removeAt(i);
        }
    }
}

void DevicesPage::refresh()
{
    const qint64 now = QDateTime::currentMSecsSinceEpoch();
    const QString keep = selectedName();
    const QSignalBlocker block(m_table);   // 줄을 다시 채우는 동안 선택 바뀜 신호를 막음

    if (m_table->rowCount() != m_order.size())
        m_table->setRowCount(int(m_order.size()));
    for (int row = 0; row < m_order.size(); ++row) {
        const QString &name = m_order[row];
        Device &d = m_devices[name];
        if (!d.fromServer) {
            // 중앙 서버 자신(hb/server)처럼 목록에 없는 것은 같은 기준으로 직접 판단
            const double age = d.lastMs ? (now - d.lastMs) / 1000.0 : 1e9;
            d.status = age < AppConfig::kHbWarnSec      ? QStringLiteral("ok")
                       : age < AppConfig::kHbOfflineSec ? QStringLiteral("warn")
                                                        : QStringLiteral("offline");
        }

        double sum = 0, mx = 0;
        int n = 0;
        for (const auto &p : d.points)
            if (p.ms >= now - 120 * 1000) {
                sum += p.interval;
                mx = std::max(mx, p.interval);
                ++n;
            }

        const QStringList cells = {
            name,
            statusText(d.status),
            d.lastMs ? QStringLiteral("%1초 전").arg((now - d.lastMs) / 1000.0, 0, 'f', 1) : QStringLiteral("받은 적 없음"),
            n ? QStringLiteral("%1초").arg(sum / n, 0, 'f', 2) : QStringLiteral("-"),
            n ? QStringLiteral("%1초").arg(mx, 0, 'f', 2) : QStringLiteral("-"),
            d.lastSeq >= 0 ? QString::number(d.missed) : QStringLiteral("-"),
            d.connectedMs ? QDateTime::fromMSecsSinceEpoch(d.connectedMs).toString(QStringLiteral("HH:mm:ss"))
                          : QStringLiteral("-"),
            extraText(d.extra),
        };
        for (int c = 0; c < ColCount; ++c) {
            auto *item = m_table->item(row, c);
            if (!item) {
                item = new QTableWidgetItem;
                m_table->setItem(row, c, item);
            }
            item->setText(cells[c]);
        }
        const QColor stateColor = d.status == QStringLiteral("ok")        ? kOk
                                  : d.status == QStringLiteral("warn")    ? kWarn
                                  : d.status == QStringLiteral("offline") ? kAlarm
                                                                          : kDim;
        m_table->item(row, ColState)->setForeground(stateColor);
        if (d.missed > 0)
            m_table->item(row, ColMissed)->setForeground(kWarn);
        else
            m_table->item(row, ColMissed)->setData(Qt::ForegroundRole, QVariant());   // 기본 글자색으로
        m_table->item(row, ColExtra)->setForeground(kDim);
        if (name == keep)
            m_table->selectRow(row);
    }
    if (keep.isEmpty() && !m_order.isEmpty())
        m_table->selectRow(0);
    showChart();
}

void DevicesPage::showChart()
{
    const QString name = selectedName();
    if (name.isEmpty()) {
        m_chartTitle->setText(QStringLiteral("<span style='color:#A0A0A0'>장치를 고르면 하트비트 간격 그래프가 나옵니다</span>"));
        m_chart->setData({}, 0);
        return;
    }
    m_chartTitle->setText(QStringLiteral("<b>%1</b>&nbsp;&nbsp;<span style='color:#A0A0A0'>하트비트 간격 (최근 2분)</span>")
                              .arg(name.toHtmlEscaped()));
    const Device &d = m_devices[name];
    m_chart->setData(d.points, d.lastMs);
}

QString DevicesPage::selectedName() const
{
    const auto rows = m_table->selectionModel()->selectedRows();
    if (rows.isEmpty())
        return {};
    const int row = rows.first().row();
    return row < m_order.size() ? m_order[row] : QString();
}

QString DevicesPage::statusText(const QString &status)
{
    if (status == QStringLiteral("ok")) return QStringLiteral("● 정상");
    if (status == QStringLiteral("warn")) return QStringLiteral("● 지연");
    if (status == QStringLiteral("offline")) return QStringLiteral("● 끊김");
    return QStringLiteral("● 확인 전");
}

QString DevicesPage::extraText(const QJsonObject &extra)
{
    QStringList parts;
    for (auto it = extra.begin(); it != extra.end(); ++it) {
        const auto v = it.value();
        const QString text = v.isString() ? v.toString()
                             : v.isBool() ? (v.toBool() ? QStringLiteral("true") : QStringLiteral("false"))
                             : v.isDouble() ? QString::number(v.toDouble())
                             : v.isArray()  ? QString::fromUtf8(QJsonDocument(v.toArray()).toJson(QJsonDocument::Compact))
                             : v.isObject() ? QString::fromUtf8(QJsonDocument(v.toObject()).toJson(QJsonDocument::Compact))
                                            : QStringLiteral("null");
        parts << it.key() + QLatin1Char(' ') + text;
    }
    return parts.isEmpty() ? QStringLiteral("-") : parts.join(QStringLiteral(" · "));
}
