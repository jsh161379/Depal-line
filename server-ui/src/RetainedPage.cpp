#include "RetainedPage.h"

#include "AppConfig.h"

#include <QHBoxLayout>
#include <QHeaderView>
#include <QJsonDocument>
#include <QLabel>
#include <QMessageBox>
#include <QPushButton>
#include <QTableWidget>
#include <QTimer>
#include <QVBoxLayout>

#include <algorithm>
#include <iterator>

namespace {

// 수정할 값
constexpr int kRebuildDelayMs = 150;     // 메시지가 몰려올 때 표 다시 그리기를 미루는 시간
constexpr int kCheckAfterDeleteMs = 800; // 지운 뒤 이만큼 기다렸다가 목록을 다시 받아 확인
constexpr int kPreviewMax = 200;         // 내용 칸에 보일 최대 글자 수

enum Col { ColTopic, ColKind, ColTime, ColBody, ColCount };

const QColor kOld(0xE2, 0xC0, 0x8D);
const QColor kDim(0xA0, 0xA0, 0xA0);

}  // namespace

RetainedPage::RetainedPage(MqttLite *mqtt, QWidget *parent)
    : QWidget(parent), m_mqtt(mqtt), m_rebuildTimer(new QTimer(this))
{
    // ── 버튼 줄 ──
    auto *bar = new QWidget(this);
    bar->setObjectName(QStringLiteral("filterBar"));
    auto *hl = new QHBoxLayout(bar);
    hl->setContentsMargins(6, 3, 6, 3);
    hl->setSpacing(6);
    m_refresh = new QPushButton(QStringLiteral("새로 고침"), bar);
    m_refresh->setToolTip(QStringLiteral("브로커에게 남은 기록을 처음부터 다시 받음"));
    m_pickOld = new QPushButton(QStringLiteral("예전 이름 고르기"), bar);
    m_pickOld->setToolTip(QStringLiteral("이름이 바뀌어 더 쓰지 않는 기록을 한 번에 고름: %1")
                              .arg(AppConfig::kOldRetainedTopics.join(QStringLiteral(", "))));
    m_pickNone = new QPushButton(QStringLiteral("고른 것 풀기"), bar);
    m_delete = new QPushButton(QStringLiteral("고른 것 지우기"), bar);
    m_delete->setObjectName(QStringLiteral("danger"));
    m_count = new QLabel(bar);
    m_count->setObjectName(QStringLiteral("key"));
    hl->addWidget(m_refresh);
    hl->addWidget(m_pickOld);
    hl->addWidget(m_pickNone);
    hl->addStretch(1);
    hl->addWidget(m_delete);
    hl->addWidget(m_count);

    // ── 표 ──
    m_table = new QTableWidget(0, ColCount, this);
    m_table->setHorizontalHeaderLabels({QStringLiteral("토픽"), QStringLiteral("구분"), QStringLiteral("받은 시각"),
                                        QStringLiteral("내용")});
    m_table->verticalHeader()->setVisible(false);
    m_table->verticalHeader()->setDefaultSectionSize(22);
    m_table->setSelectionBehavior(QAbstractItemView::SelectRows);
    m_table->setSelectionMode(QAbstractItemView::SingleSelection);
    m_table->setEditTriggers(QAbstractItemView::NoEditTriggers);
    m_table->setWordWrap(false);
    m_table->setShowGrid(false);
    m_table->setTextElideMode(Qt::ElideRight);
    m_table->setHorizontalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
    auto *header = m_table->horizontalHeader();
    header->setDefaultAlignment(Qt::AlignLeft | Qt::AlignVCenter);
    header->setHighlightSections(false);
    header->setStretchLastSection(true);
    m_table->setColumnWidth(ColTopic, 220);
    m_table->setColumnWidth(ColKind, 80);
    m_table->setColumnWidth(ColTime, 72);
    m_table->setFont(QFont(QStringLiteral("Consolas"), 9));

    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->setSpacing(0);
    layout->addWidget(bar);
    layout->addWidget(m_table, 1);

    m_rebuildTimer->setSingleShot(true);
    m_rebuildTimer->setInterval(kRebuildDelayMs);
    connect(m_rebuildTimer, &QTimer::timeout, this, &RetainedPage::rebuild);

    // 체크 상자를 누르면 고른 목록에 반영
    connect(m_table, &QTableWidget::itemChanged, this, [this](QTableWidgetItem *item) {
        if (item->column() != ColTopic)
            return;
        const QString topic = item->data(Qt::UserRole).toString();
        if (item->checkState() == Qt::Checked)
            m_checked.insert(topic);
        else
            m_checked.remove(topic);
        updateButtons();
    });
    connect(m_refresh, &QPushButton::clicked, this, &RetainedPage::refresh);
    connect(m_pickOld, &QPushButton::clicked, this, &RetainedPage::selectOld);
    connect(m_pickNone, &QPushButton::clicked, this, &RetainedPage::selectNone);
    connect(m_delete, &QPushButton::clicked, this, &RetainedPage::deleteChecked);
    connect(mqtt, &MqttLite::messageReceived, this, &RetainedPage::onMessage);
    connect(mqtt, &MqttLite::connectedChanged, this, [this](bool on, const QString &) {
        if (on) {                     // 다시 붙으면 브로커가 보관 기록을 처음부터 보내 줌 → 목록도 새로
            m_items.clear();
            m_rebuildTimer->start();
        }
        updateButtons();
    });
    rebuild();
}

bool RetainedPage::isOldName(const QString &topic)
{
    // AppConfig 목록: 정확히 같은 토픽, 또는 끝이 *이면 앞부분이 같은 토픽
    for (const auto &p : AppConfig::kOldRetainedTopics) {
        if (p.endsWith(QLatin1Char('*')) ? topic.startsWith(p.chopped(1)) : topic == p)
            return true;
    }
    return false;
}

void RetainedPage::onMessage(const MqttMessage &msg)
{
    if (msg.retained) {
        // 구독(다시 구독) 직후 브로커가 보내 주는 보관 기록
        if (msg.payload.isEmpty())
            m_items.remove(msg.topic);
        else
            m_items[msg.topic] = {msg.payload, msg.received};
    } else if (m_items.contains(msg.topic)) {
        // 보관 기록이 있는 토픽에 새 메시지 → 빈 내용이면 지워진 것, 아니면 새 값으로 바뀐 것
        if (msg.payload.isEmpty()) {
            m_items.remove(msg.topic);
        } else {
            m_items[msg.topic] = {msg.payload, msg.received};
        }
    } else {
        return;
    }
    m_rebuildTimer->start();
}

void RetainedPage::refresh()
{
    m_items.clear();
    rebuild();
    m_mqtt->resubscribe();
}

void RetainedPage::rebuild()
{
    QStringList topics = m_items.keys();
    // 예전 이름을 위로, 그다음 토픽 이름순
    std::sort(topics.begin(), topics.end(), [](const QString &a, const QString &b) {
        const bool oa = isOldName(a), ob = isOldName(b);
        return oa != ob ? oa : a < b;
    });
    // 사라진 토픽은 고른 목록에서도 뺌
    for (auto it = m_checked.begin(); it != m_checked.end();)
        it = m_items.contains(*it) ? std::next(it) : m_checked.erase(it);

    const QSignalBlocker block(m_table);   // 체크 상태를 채우는 동안 itemChanged가 불리지 않게
    m_table->setRowCount(int(topics.size()));
    for (int row = 0; row < topics.size(); ++row) {
        const QString &t = topics[row];
        const Item &it = m_items[t];
        const bool old = isOldName(t);

        auto *topic = new QTableWidgetItem(t);
        topic->setData(Qt::UserRole, t);
        topic->setFlags(topic->flags() | Qt::ItemIsUserCheckable);
        topic->setCheckState(m_checked.contains(t) ? Qt::Checked : Qt::Unchecked);
        auto *kind = new QTableWidgetItem(old ? QStringLiteral("예전 이름") : QStringLiteral("지금 씀"));
        kind->setForeground(old ? kOld : kDim);
        auto *time = new QTableWidgetItem(it.received.toString(QStringLiteral("HH:mm:ss")));
        time->setForeground(kDim);
        const auto doc = QJsonDocument::fromJson(it.payload);
        QString body = doc.isNull() ? QString::fromUtf8(it.payload).simplified()
                                    : QString::fromUtf8(doc.toJson(QJsonDocument::Compact));
        if (body.size() > kPreviewMax)
            body = body.left(kPreviewMax) + QStringLiteral("…");
        auto *bodyItem = new QTableWidgetItem(body);
        bodyItem->setToolTip(QString::fromUtf8(it.payload).left(2000));

        m_table->setItem(row, ColTopic, topic);
        m_table->setItem(row, ColKind, kind);
        m_table->setItem(row, ColTime, time);
        m_table->setItem(row, ColBody, bodyItem);
    }
    updateButtons();
}

void RetainedPage::selectOld()
{
    int n = 0;
    for (auto it = m_items.cbegin(); it != m_items.cend(); ++it)
        if (isOldName(it.key())) {
            m_checked.insert(it.key());
            ++n;
        }
    rebuild();
    emit notice(n ? QStringLiteral("예전 이름 기록 %1개를 골랐습니다").arg(n)
                  : QStringLiteral("예전 이름 기록이 없습니다"), false);
}

void RetainedPage::selectNone()
{
    m_checked.clear();
    rebuild();
}

QStringList RetainedPage::checkedTopics() const
{
    QStringList out(m_checked.cbegin(), m_checked.cend());
    out.sort();
    return out;
}

void RetainedPage::deleteChecked()
{
    const QStringList topics = checkedTopics();
    if (topics.isEmpty() || !m_mqtt->isConnected())
        return;

    // 확인 창: 지울 토픽 목록 (길면 앞 15개만), 지금 쓰는 기록이 섞였으면 주의 문장
    QStringList shown = topics.mid(0, 15);
    QString list = QStringLiteral("  ") + shown.join(QStringLiteral("\n  "));
    if (topics.size() > shown.size())
        list += QStringLiteral("\n  … 외 %1개").arg(topics.size() - shown.size());
    const int current = int(std::count_if(topics.begin(), topics.end(), [](const QString &t) { return !isOldName(t); }));
    QString text = QStringLiteral("브로커에 남은 기록 %1개를 지웁니다.\n\n%2\n").arg(topics.size()).arg(list);
    if (current > 0)
        text += QStringLiteral("\n주의: \"지금 씀\" 기록이 %1개 섞여 있습니다. 지워도 그 장치·서버가 다음에 보낼 때 다시 생기지만,"
                               "\n그 전까지는 관리자 화면 등에 상태가 비어 보일 수 있습니다.\n")
                    .arg(current);
    text += QStringLiteral("\n계속할까요?");
    if (QMessageBox::question(this, QStringLiteral("남은 기록 지우기"), text) != QMessageBox::Yes)
        return;

    // 빈 내용 + retain → 브로커가 보관 기록을 지움 (QoS 1: 브로커가 받았는지 PUBACK으로 확인)
    for (const auto &t : topics)
        m_mqtt->publish(t, QByteArray(), 1, true);
    m_checked.clear();
    emit notice(QStringLiteral("남은 기록 %1개 지우기를 보냄 — 확인 중").arg(topics.size()), false);

    // 잠시 뒤 목록을 다시 받아, 정말 사라졌는지 확인
    QTimer::singleShot(kCheckAfterDeleteMs, this, [this, topics] {
        refresh();
        QTimer::singleShot(kCheckAfterDeleteMs, this, [this, topics] {
            QStringList left;
            for (const auto &t : topics)
                if (m_items.contains(t))
                    left << t;
            if (!left.isEmpty())
                emit notice(QStringLiteral("지우지 못한 기록: %1").arg(left.join(QStringLiteral(", "))), true);
            else
                emit notice(QStringLiteral("남은 기록 지우기 완료 — 브로커에서 사라진 것을 확인했습니다"), false);
        });
    });
}

void RetainedPage::updateButtons()
{
    const bool on = m_mqtt->isConnected();
    m_refresh->setEnabled(on);
    m_pickOld->setEnabled(on && !m_items.isEmpty());
    m_pickNone->setEnabled(!m_checked.isEmpty());
    m_delete->setEnabled(on && !m_checked.isEmpty());
    const int old = int(std::count_if(m_items.keyBegin(), m_items.keyEnd(), [](const QString &t) { return isOldName(t); }));
    m_count->setText(QStringLiteral("기록 %1개 (예전 이름 %2) · 고름 %3").arg(m_items.size()).arg(old).arg(m_checked.size()));
}
