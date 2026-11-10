#include "MainWindow.h"

#include "AppConfig.h"
#include "DevicesPage.h"
#include "MessagesPage.h"
#include "MqttLite.h"
#include "PortProbe.h"
#include "ProgramsPage.h"

#include <QAction>
#include <QCheckBox>
#include <QDateTime>
#include <QHostAddress>
#include <QInputDialog>
#include <QLabel>
#include <QLineEdit>
#include <QMessageBox>
#include <QNetworkInterface>
#include <QSizePolicy>
#include <QStatusBar>
#include <QStyle>
#include <QSysInfo>
#include <QTabWidget>
#include <QToolBar>

MainWindow::MainWindow(QWidget *parent)
    : QMainWindow(parent),
      m_runner(new RunnerClient(this)),
      m_mqttProbe(new PortProbe(AppConfig::kBrokerHost, AppConfig::kBrokerPort, AppConfig::kProbeMs, this)),
      m_mysqlProbe(new PortProbe(AppConfig::kBrokerHost, AppConfig::kMySqlPort, AppConfig::kProbeMs, this))
{
    setWindowTitle(QStringLiteral("DEPAL 서버 - %1 (%2)").arg(QSysInfo::machineHostName().toUpper(), localIp()));
    resize(920, 580);
    setMinimumSize(760, 440);

    // 브로커의 모든 메시지를 받음 (#은 $SYS를 뺀 전부). 접속 이름은 PC마다 달라야 함 (같으면 서로 끊음)
    m_mqtt = new MqttLite(AppConfig::kBrokerHost, AppConfig::kBrokerPort,
                          AppConfig::kMqttClientPrefix + QSysInfo::machineHostName().toLower(),
                          {QStringLiteral("#")}, this);

    m_programs = new ProgramsPage(m_runner, this);
    m_tabs = new QTabWidget(this);
    m_tabs->setDocumentMode(true);
    m_tabs->addTab(m_programs, QStringLiteral("프로그램"));
    m_tabs->addTab(new DevicesPage(m_mqtt, this), QStringLiteral("장치 접속"));
    m_tabs->addTab(new MessagesPage(m_mqtt, this), QStringLiteral("메시지"));
    setCentralWidget(m_tabs);
    buildToolBar();
    buildStatusBar();

    connect(m_runner, &RunnerClient::statusChanged, this, &MainWindow::onStatus);
    connect(m_runner, &RunnerClient::message, this, &MainWindow::onMessage);
    connect(m_programs, &ProgramsPage::notice, this, &MainWindow::onMessage);
    connect(m_programs, &ProgramsPage::selectionChanged, this, &MainWindow::updateActions);
    connect(m_tabs, &QTabWidget::currentChanged, this, &MainWindow::updateActions);
    connect(m_mqtt, &MqttLite::connectedChanged, this, [this](bool on, const QString &reason) {
        onMessage(on ? QStringLiteral("메시지 받는 중 (장치 접속·메시지 탭)")
                     : QStringLiteral("메시지 수신 끊김: %1 → 3초마다 다시 연결").arg(reason),
                  !on);
    });
    m_mqtt->start();

    if (!m_runner->serverDirOk())
        onMessage(QStringLiteral("서버 폴더를 찾지 못했습니다 (AppConfig.h의 kServerDir 확인)"), true);
    updateActions();
}

void MainWindow::buildToolBar()
{
    auto *bar = addToolBar(QStringLiteral("도구"));
    bar->setMovable(false);
    bar->setFloatable(false);
    bar->setToolButtonStyle(Qt::ToolButtonTextBesideIcon);
    bar->setIconSize(QSize(16, 16));
    setContextMenuPolicy(Qt::NoContextMenu);   // 도구 모음 숨기기 메뉴 끔

    auto *st = style();
    m_startAll = bar->addAction(st->standardIcon(QStyle::SP_MediaPlay), QStringLiteral("전부 켜기"));
    m_stopAll = bar->addAction(st->standardIcon(QStyle::SP_MediaStop), QStringLiteral("전부 끄기"));
    bar->addSeparator();
    m_start = bar->addAction(st->standardIcon(QStyle::SP_MediaPlay), QStringLiteral("켜기"));
    m_stop = bar->addAction(st->standardIcon(QStyle::SP_MediaStop), QStringLiteral("끄기"));
    bar->addSeparator();
    m_openLog = bar->addAction(st->standardIcon(QStyle::SP_FileDialogDetailedView), QStringLiteral("로그 열기"));

    m_startAll->setShortcut(QKeySequence(Qt::Key_F9));
    m_start->setToolTip(QStringLiteral("고른 프로그램 켜기"));
    m_startAll->setToolTip(QStringLiteral("브로커·서버·컨트롤러·DB 저장 모두 켜기 (F9)"));
    m_stopAll->setToolTip(QStringLiteral("모두 끄기"));
    m_stop->setToolTip(QStringLiteral("고른 프로그램 끄기"));
    m_openLog->setToolTip(QStringLiteral("고른 프로그램의 전체 로그를 메모장으로 열기 (목록 더블클릭)"));

    auto *spacer = new QWidget(bar);
    spacer->setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Preferred);
    bar->addWidget(spacer);
    m_withFake = new QCheckBox(QStringLiteral("가짜 장치 포함  "), bar);
    m_withFake->setChecked(true);
    m_withFake->setToolTip(QStringLiteral("끄면 가짜 라파·가짜 YOLO 없이 켭니다 (실제 장치를 붙일 때)"));
    bar->addWidget(m_withFake);

    connect(m_startAll, &QAction::triggered, this, &MainWindow::startAll);
    connect(m_stopAll, &QAction::triggered, this, &MainWindow::stopAll);
    connect(m_start, &QAction::triggered, this, [this] { m_programs->requestSelected(QStringLiteral("start")); });
    connect(m_stop, &QAction::triggered, this, [this] { m_programs->requestSelected(QStringLiteral("stop")); });
    connect(m_openLog, &QAction::triggered, m_programs, &ProgramsPage::openSelectedLog);
}

void MainWindow::buildStatusBar()
{
    m_serverLabel = new QLabel(this);
    m_mqttLabel = new QLabel(this);
    m_mysqlLabel = new QLabel(this);
    m_messageLabel = new QLabel(this);
    for (auto *l : {m_serverLabel, m_mqttLabel, m_mysqlLabel})
        statusBar()->addWidget(l);
    statusBar()->addWidget(m_messageLabel, 1);
    statusBar()->setSizeGripEnabled(false);

    bindPort(m_mqttProbe, m_mqttLabel, QStringLiteral("MQTT"), AppConfig::kBrokerPort);
    bindPort(m_mysqlProbe, m_mysqlLabel, QStringLiteral("MySQL"), AppConfig::kMySqlPort);
}

void MainWindow::bindPort(PortProbe *probe, QLabel *label, const QString &name, quint16 port)
{
    auto apply = [label, name, port](bool open) {
        label->setText(QStringLiteral("%1 %2 %3").arg(name).arg(port)
                           .arg(open ? QStringLiteral("연결됨") : QStringLiteral("연결 안 됨")));
        label->setProperty("alarm", !open);
        label->setStyleSheet(open ? QString() : QStringLiteral("color: #FFD0D0; font-weight: bold;"));
    };
    apply(false);
    connect(probe, &PortProbe::stateChanged, this, apply);
}

void MainWindow::onStatus(const RunnerStatus &status)
{
    m_programs->updateStatus(status);

    QString text;
    if (status.alive()) {
        const qint64 sec = qMax<qint64>(0, status.started.secsTo(QDateTime::currentDateTime()));
        text = QStringLiteral("서버 실행 중 · %1:%2:%3")
                   .arg(sec / 3600, 2, 10, QLatin1Char('0'))
                   .arg((sec / 60) % 60, 2, 10, QLatin1Char('0'))
                   .arg(sec % 60, 2, 10, QLatin1Char('0'));
        if (status.noFake) text += QStringLiteral(" · 실제 장치");
        if (status.noDb) text += QStringLiteral(" · DB 저장 없음");
    } else if (status.fileFound && status.stale) {
        text = QStringLiteral("실행기 응답 없음");
    } else {
        text = QStringLiteral("서버 꺼짐");
    }
    m_serverLabel->setText(text);
    updateActions();
}

void MainWindow::updateActions()
{
    // 실행기 상태와 고른 줄에 맞게 버튼을 켜고 끔
    const auto &status = m_runner->status();
    const bool alive = status.alive();
    const bool dirOk = m_runner->serverDirOk();
    const QString name = m_programs->selectedName();
    const QString st = m_programs->selectedState();

    m_startAll->setEnabled(dirOk && !alive);
    m_withFake->setEnabled(!alive);
    // 응답 없는 실행기가 남긴 프로그램도 정리할 수 있게, 상태 파일이 있으면 끄기 허용
    m_stopAll->setEnabled(dirOk && (alive || (status.fileFound && status.stale)));
    const bool running = st == QStringLiteral("running");
    const bool onPrograms = m_tabs->currentWidget() == m_programs;   // 켜기·끄기·로그는 프로그램 탭에서만
    m_start->setEnabled(onPrograms && alive && !name.isEmpty() && !running);   // 꺼져 있을 때만
    m_stop->setEnabled(onPrograms && alive && running);                       // 실행 중일 때만
    m_openLog->setEnabled(onPrograms && dirOk && !name.isEmpty());
}

void MainWindow::onMessage(const QString &text, bool warning)
{
    m_messageLabel->setText(QDateTime::currentDateTime().toString(QStringLiteral("HH:mm:ss  ")) + text);
    m_messageLabel->setStyleSheet(warning ? QStringLiteral("color: #FFD0D0; font-weight: bold;") : QString());
}

void MainWindow::startAll()
{
    QString password;
    if (!RunnerClient::hasSavedDbPassword()) {
        bool ok = false;
        password = QInputDialog::getText(this, QStringLiteral("MySQL 비밀번호"),
                                         QStringLiteral("DB 저장 프로그램이 쓸 MySQL 비밀번호를 입력하세요.\n"
                                                        "비워 두면 DB 저장 없이 켭니다."),
                                         QLineEdit::Password, QString(), &ok);
        if (!ok)
            return;   // 취소
    }
    m_runner->startRunner(m_withFake->isChecked(), password);
}

void MainWindow::stopAll()
{
    if (QMessageBox::question(this, QStringLiteral("전부 끄기"),
                              QStringLiteral("브로커·중앙 서버·컨트롤러·DB 저장을 모두 끕니다.\n"
                                             "모든 장치와 관리자 프로그램의 연결이 끊깁니다. 계속할까요?"))
        == QMessageBox::Yes)
        m_runner->stopRunner();
}

QString MainWindow::localIp()
{
    // 학원 네트워크(192.168.*) 주소를 먼저, 없으면 루프백이 아닌 IPv4 아무거나
    QString fallback;
    for (const auto &addr : QNetworkInterface::allAddresses()) {
        if (addr.protocol() != QAbstractSocket::IPv4Protocol || addr.isLoopback())
            continue;
        const QString ip = addr.toString();
        if (ip.startsWith(QStringLiteral("192.168.")))
            return ip;
        if (fallback.isEmpty() && !ip.startsWith(QStringLiteral("169.254.")))
            fallback = ip;
    }
    return fallback.isEmpty() ? QStringLiteral("IP 없음") : fallback;
}
