#include "MessagesPage.h"

#include "AppConfig.h"

#include <QCheckBox>
#include <QHBoxLayout>
#include <QHeaderView>
#include <QItemSelectionModel>
#include <QJsonDocument>
#include <QLabel>
#include <QLineEdit>
#include <QPlainTextEdit>
#include <QPushButton>
#include <QRegularExpression>
#include <QScrollBar>
#include <QSplitter>
#include <QSyntaxHighlighter>
#include <QTableWidget>
#include <QVBoxLayout>

#include <vector>

namespace {

// 수정할 값
constexpr int kMaxRows = 2000;        // 표에 보여 줄 최대 줄 수
constexpr int kOneLineMax = 400;      // 표 한 칸에 넣을 최대 글자 수

enum Col { ColTime, ColTopic, ColBody, ColCount };

const QColor kCmd(0x4F, 0xA3, 0xE3);
const QColor kAlert(0x4E, 0xC9, 0xB0);
const QColor kEvent(0xE2, 0xC0, 0x8D);
const QColor kDim(0xA0, 0xA0, 0xA0);

// JSON 색칠 (VS Code 어두운 테마 색)
class JsonHighlighter : public QSyntaxHighlighter
{
public:
    explicit JsonHighlighter(QTextDocument *doc) : QSyntaxHighlighter(doc)
    {
        auto fmt = [](const QColor &c) { QTextCharFormat f; f.setForeground(c); return f; };
        // 나중 규칙이 앞 규칙을 덮어씀: 숫자 → 참·거짓·null → 문자열 → 키
        m_rules = {
            {QRegularExpression(QStringLiteral(R"(-?\b\d+(\.\d+)?([eE][+-]?\d+)?\b)")), fmt(QColor(0xB5, 0xCE, 0xA8))},
            {QRegularExpression(QStringLiteral(R"(\b(true|false|null)\b)")), fmt(QColor(0x56, 0x9C, 0xD6))},
            {QRegularExpression(QStringLiteral(R"("(?:[^"\\]|\\.)*")")), fmt(QColor(0xCE, 0x91, 0x78))},
            {QRegularExpression(QStringLiteral(R"("(?:[^"\\]|\\.)*"(?=\s*:))")), fmt(QColor(0x9C, 0xDC, 0xFE))},
        };
    }

protected:
    void highlightBlock(const QString &text) override
    {
        for (const auto &[re, format] : m_rules) {
            auto it = re.globalMatch(text);
            while (it.hasNext()) {
                const auto m = it.next();
                setFormat(int(m.capturedStart()), int(m.capturedLength()), format);
            }
        }
    }

private:
    QVector<QPair<QRegularExpression, QTextCharFormat>> m_rules;
};

}  // namespace

MessagesPage::MessagesPage(MqttLite *mqtt, QWidget *parent) : QWidget(parent)
{
    const QFont mono = QFont(QStringLiteral("Consolas"), 9);

    // ── 보기 줄 ──
    auto *bar = new QWidget(this);
    bar->setObjectName(QStringLiteral("filterBar"));
    auto *hl = new QHBoxLayout(bar);
    hl->setContentsMargins(6, 3, 6, 3);
    hl->setSpacing(8);
    auto *see = new QLabel(QStringLiteral("보기"), bar);
    see->setObjectName(QStringLiteral("key"));
    hl->addWidget(see);
    const QString names[KindCount] = {QStringLiteral("명령·응답"), QStringLiteral("알림"), QStringLiteral("결과·이벤트"),
                                      QStringLiteral("라인 상태"), QStringLiteral("하트비트"), QStringLiteral("센서"),
                                      QStringLiteral("기타")};
    const QString tips[KindCount] = {QStringLiteral("cmd/*, res/*, ui/cmd"), QStringLiteral("event/*, vision/result"),
                                     QStringLiteral("server/results, server/events"),
                                     QStringLiteral("server/line, server/devices, status/*"), QStringLiteral("hb/* (1초마다, 많음)"),
                                     QStringLiteral("sensor/*"), QStringLiteral("위에 없는 토픽")};
    for (int k = 0; k < KindCount; ++k) {
        m_kinds[k] = new QCheckBox(names[k], bar);
        m_kinds[k]->setToolTip(tips[k]);
        m_kinds[k]->setChecked(k != KindHeartbeat && k != KindSensor);   // 많이 오는 것은 처음에 숨김
        connect(m_kinds[k], &QCheckBox::toggled, this, &MessagesPage::rebuild);
        if (k != KindOther)           // "기타"는 자리를 아끼려고 숨김 (항상 보임)
            hl->addWidget(m_kinds[k]);
        else
            m_kinds[k]->hide();
    }
    m_search = new QLineEdit(bar);
    m_search->setPlaceholderText(QStringLiteral("토픽·내용 찾기"));
    m_search->setClearButtonEnabled(true);
    m_search->setFixedWidth(170);
    connect(m_search, &QLineEdit::textChanged, this, &MessagesPage::rebuild);
    hl->addWidget(m_search);
    hl->addStretch(1);
    m_pause = new QPushButton(QStringLiteral("멈춤"), bar);
    m_pause->setCheckable(true);
    m_pause->setToolTip(QStringLiteral("표를 멈춤 (메시지는 계속 받아 둠)"));
    connect(m_pause, &QPushButton::toggled, this, [this](bool on) {
        m_pause->setText(on ? QStringLiteral("이어 보기") : QStringLiteral("멈춤"));
        if (!on)
            rebuild();
    });
    auto *clear = new QPushButton(QStringLiteral("지우기"), bar);
    connect(clear, &QPushButton::clicked, this, [this] {
        m_entries.clear();
        rebuild();
    });
    m_count = new QLabel(bar);
    m_count->setObjectName(QStringLiteral("key"));
    hl->addWidget(m_pause);
    hl->addWidget(clear);
    hl->addWidget(m_count);

    // ── 표 ──
    m_table = new QTableWidget(0, ColCount, this);
    m_table->setHorizontalHeaderLabels({QStringLiteral("시각"), QStringLiteral("토픽"), QStringLiteral("내용")});
    m_table->verticalHeader()->setVisible(false);
    m_table->verticalHeader()->setDefaultSectionSize(20);
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
    m_table->setColumnWidth(ColTime, 92);
    m_table->setColumnWidth(ColTopic, 120);
    m_table->setFont(mono);
    connect(m_table, &QTableWidget::itemSelectionChanged, this, &MessagesPage::showDetail);

    // ── 상세 ──
    auto *bottom = new QWidget(this);
    m_detailHead = new QLabel(bottom);
    m_detailHead->setObjectName(QStringLiteral("detailHead"));
    m_detailHead->setTextInteractionFlags(Qt::TextSelectableByMouse);
    m_detail = new QPlainTextEdit(bottom);
    m_detail->setReadOnly(true);
    m_detail->setFont(mono);
    m_detail->setLineWrapMode(QPlainTextEdit::NoWrap);
    m_detail->setPlaceholderText(QStringLiteral("표에서 메시지를 고르면 내용이 여기에 펼쳐집니다."));
    new JsonHighlighter(m_detail->document());
    auto *bl = new QVBoxLayout(bottom);
    bl->setContentsMargins(0, 0, 0, 0);
    bl->setSpacing(0);
    bl->addWidget(m_detailHead);
    bl->addWidget(m_detail, 1);

    auto *split = new QSplitter(Qt::Vertical, this);
    split->addWidget(m_table);
    split->addWidget(bottom);
    split->setChildrenCollapsible(false);
    split->setStretchFactor(0, 3);
    split->setStretchFactor(1, 2);

    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->setSpacing(0);
    layout->addWidget(bar);
    layout->addWidget(split, 1);

    connect(mqtt, &MqttLite::messageReceived, this, &MessagesPage::onMessage);
    showDetail();
    updateCount();
}

MessagesPage::Kind MessagesPage::kindOf(const QString &t)
{
    if (t.startsWith(QStringLiteral("cmd/")) || t.startsWith(QStringLiteral("res/")) || t == QStringLiteral("ui/cmd"))
        return KindCommand;
    if (t.startsWith(QStringLiteral("event/")) || t.startsWith(QStringLiteral("vision/")))
        return KindAlert;
    if (t == QStringLiteral("server/results") || t == QStringLiteral("server/events"))
        return KindResult;
    if (t == QStringLiteral("server/line") || t == QStringLiteral("server/devices") || t.startsWith(QStringLiteral("status/")))
        return KindLine;
    if (t.startsWith(QStringLiteral("hb/")))
        return KindHeartbeat;
    if (t.startsWith(QStringLiteral("sensor/")))
        return KindSensor;
    return KindOther;
}

QColor MessagesPage::colorOf(Kind kind, const QString &topic)
{
    switch (kind) {
    case KindCommand: return topic.startsWith(QStringLiteral("res/")) ? QColor() : kCmd;   // 응답은 기본 글자색
    case KindAlert: return kAlert;
    case KindResult: return kEvent;
    case KindLine:
    case KindHeartbeat:
    case KindSensor: return kDim;
    default: return QColor();
    }
}

void MessagesPage::onMessage(const MqttMessage &msg)
{
    Entry e{m_nextId++, msg, kindOf(msg.topic), {}};
    const auto doc = QJsonDocument::fromJson(msg.payload);
    e.oneLine = doc.isNull() ? QString::fromUtf8(msg.payload).simplified()
                             : QString::fromUtf8(doc.toJson(QJsonDocument::Compact));
    if (e.oneLine.size() > kOneLineMax)
        e.oneLine = e.oneLine.left(kOneLineMax) + QStringLiteral("…");
    m_entries.push_back(e);
    while (int(m_entries.size()) > AppConfig::kMaxMessages)
        m_entries.pop_front();

    if (!m_pause->isChecked() && visible(m_entries.back()))
        appendRow(m_entries.back());
    updateCount();
}

bool MessagesPage::visible(const Entry &e) const
{
    if (e.kind != KindOther && !m_kinds[e.kind]->isChecked())
        return false;
    const QString q = m_search->text().trimmed();
    return q.isEmpty() || e.msg.topic.contains(q, Qt::CaseInsensitive) || e.oneLine.contains(q, Qt::CaseInsensitive);
}

void MessagesPage::appendRow(const Entry &e)
{
    auto *bar = m_table->verticalScrollBar();
    const bool atBottom = bar->value() >= bar->maximum() - 2;   // 맨 아래를 보고 있을 때만 따라 내려감

    if (m_table->rowCount() >= kMaxRows) {
        m_table->removeRow(0);
        m_rowIds.removeFirst();
    }
    const int row = m_table->rowCount();
    m_table->insertRow(row);
    m_rowIds.append(e.id);

    auto *time = new QTableWidgetItem(e.msg.received.toString(QStringLiteral("HH:mm:ss.zzz")));
    time->setForeground(kDim);
    auto *topic = new QTableWidgetItem(e.msg.retained ? e.msg.topic + QStringLiteral(" (보관)") : e.msg.topic);
    const QColor c = colorOf(e.kind, e.msg.topic);
    if (c.isValid())
        topic->setForeground(c);
    if (e.kind == KindResult && e.oneLine.contains(QStringLiteral("\"level\":\"ERROR\"")))
        topic->setForeground(QColor(0xF4, 0x87, 0x71));
    m_table->setItem(row, ColTime, time);
    m_table->setItem(row, ColTopic, topic);
    m_table->setItem(row, ColBody, new QTableWidgetItem(e.oneLine));
    if (atBottom)
        m_table->scrollToBottom();
}

void MessagesPage::rebuild()
{
    if (m_pause->isChecked())
        return;
    const QSignalBlocker block(m_table);
    m_table->setRowCount(0);
    m_rowIds.clear();
    // 뒤에서부터 보이는 것만 kMaxRows개 모아서 순서대로 넣음
    std::vector<const Entry *> pick;
    for (auto it = m_entries.rbegin(); it != m_entries.rend() && int(pick.size()) < kMaxRows; ++it)
        if (visible(*it))
            pick.push_back(&*it);
    for (auto it = pick.rbegin(); it != pick.rend(); ++it)
        appendRow(**it);
    m_table->scrollToBottom();
    showDetail();
    updateCount();
}

const MessagesPage::Entry *MessagesPage::entry(qint64 id) const
{
    if (m_entries.empty() || id < m_entries.front().id || id > m_entries.back().id)
        return nullptr;
    return &m_entries[size_t(id - m_entries.front().id)];   // id는 1씩 늘어나므로 위치 계산 가능
}

void MessagesPage::showDetail()
{
    const auto rows = m_table->selectionModel()->selectedRows();
    const Entry *e = rows.isEmpty() || rows.first().row() >= m_rowIds.size() ? nullptr
                                                                             : entry(m_rowIds[rows.first().row()]);
    if (!e) {
        m_detailHead->setText(QStringLiteral("<span style='color:#A0A0A0'>고른 메시지 없음</span>"));
        m_detail->clear();
        return;
    }
    const QColor c = colorOf(e->kind, e->msg.topic);
    m_detailHead->setText(QStringLiteral("<span style='color:%1'>%2</span>&nbsp;&nbsp;"
                                         "<span style='color:#A0A0A0'>%3 · %4바이트 · QoS %5%6</span>")
                              .arg(c.isValid() ? c.name() : QStringLiteral("#F1F1F1"), e->msg.topic.toHtmlEscaped(),
                                   e->msg.received.toString(QStringLiteral("HH:mm:ss.zzz")))
                              .arg(e->msg.payload.size())
                              .arg(e->msg.qos)
                              .arg(e->msg.retained ? QStringLiteral(" · 브로커 보관(retained)") : QString()));
    const auto doc = QJsonDocument::fromJson(e->msg.payload);
    m_detail->setPlainText(doc.isNull() ? QString::fromUtf8(e->msg.payload)
                                        : QString::fromUtf8(doc.toJson(QJsonDocument::Indented)).trimmed());
}

void MessagesPage::updateCount()
{
    m_count->setText(QStringLiteral("%1건 (표 %2)").arg(m_entries.size()).arg(m_table->rowCount()));
}
