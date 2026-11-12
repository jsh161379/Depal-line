#include "ProgramsPage.h"

#include <QDesktopServices>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFont>
#include <QFormLayout>
#include <QFrame>
#include <QHBoxLayout>
#include <QHeaderView>
#include <QLabel>
#include <QMessageBox>
#include <QPlainTextEdit>
#include <QScrollBar>
#include <QSplitter>
#include <QTableWidget>
#include <QUrl>
#include <QVBoxLayout>

namespace {

enum Column { ColName, ColState, ColPid, ColUptime, ColExit, ColDesc, ColCount };

// 색은 상태에만 (main.cpp의 팔레트와 맞춤)
const QColor kRun(0x57, 0xC2, 0x6B);     // 실행 중: 초록
const QColor kMuted(0x9D, 0x9D, 0x9D);   // 중지됨, 설명: 회색
const QColor kError(0xF0, 0x5D, 0x5D);   // 오류 종료: 빨강
const QColor kWarn(0xD7, 0xBA, 0x7D);    // 로그 경고 줄: 노랑

constexpr int kTailBytes = 64 * 1024;    // 프로그램을 고르면 로그 끝에서 이만큼 읽음 (대략 수백 줄)
constexpr int kMaxLogLines = 2000;       // 로그 칸에 남겨 둘 최대 줄 수

QTableWidgetItem *item()
{
    auto *it = new QTableWidgetItem;
    it->setFlags(Qt::ItemIsEnabled | Qt::ItemIsSelectable);
    return it;
}

QString stateText(const QString &state)
{
    if (state == QStringLiteral("running")) return QStringLiteral("실행 중");
    if (state == QStringLiteral("exited")) return QStringLiteral("오류 종료");
    if (state == QStringLiteral("stopped")) return QStringLiteral("중지됨");
    return {};
}

QColor stateColor(const QString &state)
{
    if (state == QStringLiteral("running")) return kRun;
    if (state == QStringLiteral("exited")) return kError;
    return kMuted;
}

}  // namespace

ProgramsPage::ProgramsPage(RunnerClient *runner, QWidget *parent)
    : QWidget(parent), m_runner(runner)
{
    const QFont mono = QFont(QStringLiteral("Consolas"), 9);

    // ── 목록 ──
    m_table = new QTableWidget(0, ColCount, this);
    m_table->setHorizontalHeaderLabels({QStringLiteral("이름"), QStringLiteral("상태"), QStringLiteral("PID"),
                                        QStringLiteral("가동 시간"), QStringLiteral("종료 코드"), QStringLiteral("설명")});
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
    m_table->setColumnWidth(ColName, 100);
    m_table->setColumnWidth(ColState, 80);
    m_table->setColumnWidth(ColPid, 64);
    m_table->setColumnWidth(ColUptime, 80);
    m_table->setColumnWidth(ColExit, 70);
    connect(m_table, &QTableWidget::itemSelectionChanged, this, [this] {
        showDetail();
        refreshLog(true);
        emit selectionChanged();
    });
    connect(m_table, &QTableWidget::cellDoubleClicked, this, [this] { openSelectedLog(); });

    // ── 상세: 왼쪽 정보 ──
    auto *info = new QWidget(this);
    info->setObjectName(QStringLiteral("detailInfo"));
    m_detailName = new QLabel(info);
    QFont bold = m_detailName->font();
    bold.setBold(true);
    m_detailName->setFont(bold);
    m_detailState = new QLabel(info);
    m_detailPid = new QLabel(info);
    m_detailStarted = new QLabel(info);
    m_detailLog = new QLabel(info);
    m_detailLog->setTextInteractionFlags(Qt::TextSelectableByMouse);
    auto *form = new QFormLayout;
    form->setContentsMargins(0, 4, 0, 0);
    form->setHorizontalSpacing(12);
    form->setVerticalSpacing(3);
    form->setLabelAlignment(Qt::AlignLeft);
    auto addRow = [form, info](const QString &name, QLabel *value) {
        auto *key = new QLabel(name, info);
        key->setObjectName(QStringLiteral("key"));
        form->addRow(key, value);
    };
    addRow(QStringLiteral("상태"), m_detailState);
    addRow(QStringLiteral("PID"), m_detailPid);
    addRow(QStringLiteral("시작 시각"), m_detailStarted);
    addRow(QStringLiteral("로그 파일"), m_detailLog);
    auto *iv = new QVBoxLayout(info);
    iv->setContentsMargins(8, 6, 8, 6);
    iv->addWidget(m_detailName);
    iv->addLayout(form);
    iv->addStretch(1);
    info->setMinimumWidth(220);
    info->setMaximumWidth(300);

    // ── 상세: 오른쪽 로그 ──
    m_logView = new QPlainTextEdit(this);
    m_logView->setReadOnly(true);
    m_logView->setLineWrapMode(QPlainTextEdit::NoWrap);
    m_logView->setMaximumBlockCount(kMaxLogLines);
    m_logView->setFont(mono);
    m_logView->setPlaceholderText(QStringLiteral("목록에서 프로그램을 고르면 로그 마지막 부분이 여기에 나옵니다."));

    auto *detail = new QWidget(this);
    auto *dh = new QHBoxLayout(detail);
    dh->setContentsMargins(0, 0, 0, 0);
    dh->setSpacing(0);
    dh->addWidget(info);
    auto *line = new QFrame(detail);
    line->setFrameShape(QFrame::VLine);
    line->setObjectName(QStringLiteral("divider"));
    dh->addWidget(line);
    dh->addWidget(m_logView, 1);

    auto *split = new QSplitter(Qt::Vertical, this);
    split->addWidget(m_table);
    split->addWidget(detail);
    split->setStretchFactor(0, 0);
    split->setStretchFactor(1, 1);
    split->setSizes({190, 260});
    split->setChildrenCollapsible(false);

    auto *layout = new QVBoxLayout(this);
    layout->setContentsMargins(0, 0, 0, 0);
    layout->addWidget(split);

    showDetail();
}

QString ProgramsPage::label(const QString &name)
{
    static const QHash<QString, QString> labels = {
        {QStringLiteral("broker"), QStringLiteral("MQTT 브로커 (Mosquitto)")},
        {QStringLiteral("server"), QStringLiteral("중앙 서버 - 장치 감시, 하트비트")},
        {QStringLiteral("controller"), QStringLiteral("라인 컨트롤러 - 명령, 등급 판정")},
        {QStringLiteral("db_writer"), QStringLiteral("DB 저장 프로그램 (MySQL)")},
        {QStringLiteral("fake_arm1"), QStringLiteral("가짜 로봇팔 1 (시험용)")},
        {QStringLiteral("fake_yolo"), QStringLiteral("가짜 YOLO (시험용)")},
    };
    return labels.value(name, name);
}

QString ProgramsPage::uptime(const QDateTime &started)
{
    if (!started.isValid())
        return {};
    const qint64 sec = qMax<qint64>(0, started.secsTo(QDateTime::currentDateTime()));
    return QStringLiteral("%1:%2:%3")
        .arg(sec / 3600, 2, 10, QLatin1Char('0'))
        .arg((sec / 60) % 60, 2, 10, QLatin1Char('0'))
        .arg(sec % 60, 2, 10, QLatin1Char('0'));
}

void ProgramsPage::rebuildRows(const QList<ProgramInfo> &programs)
{
    const QString keep = selectedName();
    const QFont mono = QFont(QStringLiteral("Consolas"), 9);
    m_table->blockSignals(true);
    m_table->setRowCount(0);
    m_names.clear();
    for (const auto &p : programs) {
        const int row = m_table->rowCount();
        m_table->insertRow(row);
        m_names << p.name;
        for (int c = 0; c < ColCount; ++c)
            m_table->setItem(row, c, item());
        m_table->item(row, ColName)->setText(p.name);
        m_table->item(row, ColDesc)->setText(label(p.name));
        m_table->item(row, ColDesc)->setForeground(kMuted);
        for (int c : {ColPid, ColUptime, ColExit})
            m_table->item(row, c)->setFont(mono);
    }
    m_table->blockSignals(false);
    // 고른 줄 유지 (없으면 첫 줄)
    const int row = m_names.indexOf(keep);
    if (!m_names.isEmpty())
        m_table->selectRow(row >= 0 ? row : 0);
}

void ProgramsPage::updateStatus(const RunnerStatus &status)
{
    QStringList names;
    for (const auto &p : status.programs)
        names << p.name;
    m_alive = status.alive();
    m_programs.clear();
    for (const auto &p : status.programs)
        m_programs.insert(p.name, p);
    if (names != m_names)
        rebuildRows(status.programs);

    for (int row = 0; row < status.programs.size(); ++row) {
        const auto &p = status.programs.at(row);
        const QString st = m_alive ? p.state : QString();
        const bool running = st == QStringLiteral("running");
        auto *stateItem = m_table->item(row, ColState);
        stateItem->setText(stateText(st));
        stateItem->setForeground(stateColor(st));
        m_table->item(row, ColPid)->setText(running ? QString::number(p.pid) : QString());
        m_table->item(row, ColUptime)->setText(running ? uptime(p.started) : QString());
        m_table->item(row, ColExit)->setText(!running && p.hasExitCode && st == QStringLiteral("exited")
                                                 ? QString::number(p.exitCode) : QString());
    }

    showDetail();
    refreshLog(false);
}

QString ProgramsPage::selectedName() const
{
    const auto rows = m_table->selectionModel()->selectedRows();
    if (rows.isEmpty())
        return {};
    const int row = rows.first().row();
    return row < m_names.size() ? m_names.at(row) : QString();
}

QString ProgramsPage::selectedState() const
{
    return m_alive ? m_programs.value(selectedName()).state : QString();
}

void ProgramsPage::showDetail()
{
    const QString name = selectedName();
    if (name.isEmpty()) {
        m_detailName->setText(QStringLiteral("(선택 없음)"));
        for (auto *l : {m_detailState, m_detailPid, m_detailStarted, m_detailLog})
            l->clear();
        return;
    }
    const auto p = m_programs.value(name);
    const QString st = m_alive ? p.state : QString();
    m_detailName->setText(name);

    QString stText = st.isEmpty() ? QStringLiteral("-") : stateText(st);
    if (st == QStringLiteral("exited") && p.hasExitCode)
        stText += QStringLiteral(" (코드 %1)").arg(p.exitCode);
    m_detailState->setText(stText);
    m_detailState->setStyleSheet(QStringLiteral("color: %1;").arg(stateColor(st).name()));

    const bool running = st == QStringLiteral("running");
    m_detailPid->setText(running ? QString::number(p.pid) : QStringLiteral("-"));
    m_detailStarted->setText(p.started.isValid() ? p.started.toString(QStringLiteral("yyyy-MM-dd HH:mm:ss"))
                                                 : QStringLiteral("-"));
    QString log = p.log.isEmpty() ? QStringLiteral("logs/%1.log").arg(name) : p.log;
    m_detailLog->setText(log.replace(QLatin1Char('/'), QLatin1Char('\\')));
}

// 로그 이어 읽기: reload이면 파일 끝 부분을 새로 읽고, 아니면 늘어난 부분만 붙임
void ProgramsPage::refreshLog(bool reload)
{
    const QString name = selectedName();
    if (name.isEmpty() || !m_runner->serverDirOk()) {
        m_logView->clear();
        m_logPath.clear();
        return;
    }
    const QString rel = m_programs.value(name).log.isEmpty() ? QStringLiteral("logs/%1.log").arg(name)
                                                              : m_programs.value(name).log;
    const QString path = m_runner->serverDir() + QLatin1Char('/') + rel;

    QFile file(path);
    if (!file.open(QIODevice::ReadOnly)) {
        if (reload || path != m_logPath) {
            m_logView->setPlainText(QStringLiteral("로그 파일이 아직 없습니다: %1").arg(QDir::toNativeSeparators(path)));
            m_logPath = path;
            m_logPos = 0;
            m_logRest.clear();
        }
        return;
    }
    const qint64 size = file.size();
    // 다른 프로그램을 골랐거나, 파일이 줄어들었으면(다시 켜져 새 로그) 처음부터
    if (reload || path != m_logPath || size < m_logPos) {
        m_logView->clear();
        m_logPath = path;
        m_logRest.clear();
        m_logPos = qMax<qint64>(0, size - kTailBytes);
        if (m_logPos > 0) {
            file.seek(m_logPos);
            file.readLine();            // 잘린 첫 줄은 버림
            m_logPos = file.pos();
        }
    }
    if (size == m_logPos)
        return;
    file.seek(m_logPos);
    QByteArray chunk = m_logRest + file.readAll();
    m_logPos = file.pos();
    const int lastNl = chunk.lastIndexOf('\n');
    if (lastNl < 0) {           // 아직 한 줄도 다 안 옴
        m_logRest = chunk;
        return;
    }
    m_logRest = chunk.mid(lastNl + 1);
    chunk.truncate(lastNl);
    appendLogLines(QString::fromUtf8(chunk));
}

void ProgramsPage::appendLogLines(const QString &text)
{
    // 맨 아래를 보고 있을 때만 따라 내려감 (위를 읽는 중이면 화면을 움직이지 않음)
    auto *bar = m_logView->verticalScrollBar();
    const bool atBottom = bar->value() >= bar->maximum() - 2;

    static const QStringList errorWords = {QStringLiteral("Traceback"), QStringLiteral("Error"),
                                           QStringLiteral("ERROR"), QStringLiteral("Exception"),
                                           QStringLiteral("  File \""), QStringLiteral("종료됨")};
    static const QStringList warnWords = {QStringLiteral("WARN"), QStringLiteral("Warning")};

    for (QString lineText : text.split(QLatin1Char('\n'))) {
        if (lineText.endsWith(QLatin1Char('\r')))
            lineText.chop(1);
        QString color;
        for (const auto &w : errorWords)
            if (lineText.contains(w)) { color = kError.name(); break; }
        if (color.isEmpty())
            for (const auto &w : warnWords)
                if (lineText.contains(w)) { color = kWarn.name(); break; }
        const QString esc = lineText.toHtmlEscaped().replace(QLatin1Char(' '), QStringLiteral("&nbsp;"));
        m_logView->appendHtml(color.isEmpty() ? esc : QStringLiteral("<span style='color:%1'>%2</span>").arg(color, esc));
    }
    if (atBottom)
        bar->setValue(bar->maximum());
}

void ProgramsPage::requestSelected(const QString &action)
{
    const QString name = selectedName();
    if (name.isEmpty())
        return;
    if (action != QStringLiteral("start")) {
        QString warn;
        if (name == QStringLiteral("broker"))
            warn = QStringLiteral("\n\n브로커를 끄면 모든 장치와 관리자 프로그램의 연결이 끊깁니다.");
        else if (name == QStringLiteral("controller"))
            warn = QStringLiteral("\n\n진행 중인 회차가 있으면 중단됩니다.");
        const QString verb = action == QStringLiteral("stop") ? QStringLiteral("끕니다") : QStringLiteral("다시 켭니다");
        if (QMessageBox::question(this, QStringLiteral("확인"), QStringLiteral("%1 을(를) %2.%3").arg(name, verb, warn))
            != QMessageBox::Yes)
            return;
    }
    m_runner->sendRequest(action, name);
}

void ProgramsPage::openSelectedLog()
{
    const QString name = selectedName();
    if (name.isEmpty())
        return;
    const QString rel = m_programs.value(name).log.isEmpty() ? QStringLiteral("logs/%1.log").arg(name)
                                                              : m_programs.value(name).log;
    const QString path = m_runner->serverDir() + QLatin1Char('/') + rel;
    if (!QFileInfo::exists(path)) {
        emit notice(QStringLiteral("로그 파일이 없습니다: %1").arg(QDir::toNativeSeparators(path)), true);
        return;
    }
    QDesktopServices::openUrl(QUrl::fromLocalFile(path));   // 전체 로그를 메모장 등으로
}
