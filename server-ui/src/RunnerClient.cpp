#include "RunnerClient.h"

#include "AppConfig.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QHash>
#include <QJsonArray>
#include <QJsonDocument>
#include <QProcess>
#include <QProcessEnvironment>
#include <QSaveFile>
#include <QUuid>

namespace {

const QString kStatusFile = QStringLiteral("run_all.status.json");
const QString kRequestFile = QStringLiteral("run_all.request.json");
const QString kTimeFormat = QStringLiteral("yyyy-MM-dd HH:mm:ss");

// 창 없이 실행 (Windows CREATE_NO_WINDOW)
void hideConsoleWindow(QProcess &process)
{
#ifdef Q_OS_WIN
    process.setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *args) {
        args->flags |= 0x08000000;  // CREATE_NO_WINDOW
    });
#else
    Q_UNUSED(process);
#endif
}

QProcessEnvironment pythonEnvironment()
{
    auto env = QProcessEnvironment::systemEnvironment();
    env.insert(QStringLiteral("PYTHONIOENCODING"), QStringLiteral("utf-8"));
    return env;
}

const QHash<QString, QString> &actionNames()
{
    static const QHash<QString, QString> names = {
        {QStringLiteral("start"), QStringLiteral("켜기")},
        {QStringLiteral("stop"), QStringLiteral("끄기")},
        {QStringLiteral("restart"), QStringLiteral("다시 켜기")},
    };
    return names;
}

}  // namespace

RunnerClient::RunnerClient(QObject *parent)
    : QObject(parent), m_serverDir(findServerDir())
{
    connect(&m_timer, &QTimer::timeout, this, &RunnerClient::poll);
    m_timer.start(AppConfig::kPollMs);
    QTimer::singleShot(0, this, &RunnerClient::poll);
}

QString RunnerClient::findServerDir()
{
    // 1) 설정한 폴더  2) 실행 파일 위쪽 폴더들에서 server/run_all.py 찾기
    if (QFileInfo::exists(AppConfig::kServerDir + QStringLiteral("/run_all.py")))
        return QDir(AppConfig::kServerDir).absolutePath();

    QDir dir(QCoreApplication::applicationDirPath());
    for (int i = 0; i < 6; ++i) {
        if (dir.exists(QStringLiteral("server/run_all.py")))
            return dir.absoluteFilePath(QStringLiteral("server"));
        if (!dir.cdUp())
            break;
    }
    return {};
}

QString RunnerClient::logsPath(const QString &file) const
{
    return m_serverDir + QStringLiteral("/logs/") + file;
}

bool RunnerClient::hasSavedDbPassword()
{
    return !qEnvironmentVariableIsEmpty("AA_DB_PASSWORD");
}

bool RunnerClient::startRunner(bool withFakeDevices, const QString &password)
{
    if (!serverDirOk()) {
        emit message(QStringLiteral("서버 폴더(run_all.py)를 찾지 못했습니다. AppConfig.h의 kServerDir 확인"), true);
        return false;
    }
    if (m_status.alive()) {
        emit message(QStringLiteral("이미 실행 중입니다."), true);
        return false;
    }

    QStringList args = {QStringLiteral("run_all.py"), QStringLiteral("--hidden")};
    if (!withFakeDevices)
        args << QStringLiteral("--no-fake");

    auto env = pythonEnvironment();
    if (!password.isEmpty())
        env.insert(QStringLiteral("AA_DB_PASSWORD"), password);
    else if (!hasSavedDbPassword())
        args << QStringLiteral("--no-db");   // 비밀번호가 없으면 DB 저장 없이 켬

    QProcess process;
    process.setProgram(AppConfig::kPython);
    process.setArguments(args);
    process.setWorkingDirectory(m_serverDir);
    process.setProcessEnvironment(env);
    hideConsoleWindow(process);

    // 서버 UI와 따로 사는 프로세스로 켬 → 서버 UI가 꺼져도 서버는 계속 돎
    qint64 pid = 0;
    if (!process.startDetached(&pid)) {
        emit message(QStringLiteral("실행기를 켜지 못했습니다: %1 (python이 PATH에 있는지 확인)")
                         .arg(process.errorString()), true);
        return false;
    }
    emit message(QStringLiteral("실행기를 켜는 중... (PID %1, 옵션: %2)").arg(pid).arg(args.mid(1).join(QLatin1Char(' '))), false);
    return true;
}

void RunnerClient::stopRunner()
{
    if (!serverDirOk())
        return;

    auto *process = new QProcess(this);
    process->setProgram(AppConfig::kPython);
    process->setArguments({QStringLiteral("run_all.py"), QStringLiteral("--stop")});
    process->setWorkingDirectory(m_serverDir);
    process->setProcessEnvironment(pythonEnvironment());
    process->setProcessChannelMode(QProcess::MergedChannels);
    hideConsoleWindow(*process);

    connect(process, &QProcess::finished, this, [this, process](int code, QProcess::ExitStatus) {
        const QString out = QString::fromUtf8(process->readAll()).trimmed().replace(QLatin1Char('\n'), QStringLiteral(" / "));
        emit message(out.isEmpty() ? QStringLiteral("끄기 완료 (코드 %1)").arg(code) : out, code != 0);
        process->deleteLater();
    });
    connect(process, &QProcess::errorOccurred, this, [this, process](QProcess::ProcessError) {
        emit message(QStringLiteral("끄기 실패: %1").arg(process->errorString()), true);
        process->deleteLater();
    });

    emit message(QStringLiteral("서버를 끄는 중..."), false);
    process->start();
}

bool RunnerClient::sendRequest(const QString &action, const QString &name)
{
    if (!m_status.alive()) {
        emit message(QStringLiteral("실행기가 켜져 있지 않습니다."), true);
        return false;
    }
    if (!m_pendingId.isEmpty() || QFile::exists(logsPath(kRequestFile))) {
        emit message(QStringLiteral("앞의 요청을 처리하는 중입니다. 잠시 뒤 다시 누르세요."), true);
        return false;
    }

    const QString id = QUuid::createUuid().toString(QUuid::WithoutBraces);
    const QJsonObject req{{QStringLiteral("id"), id},
                          {QStringLiteral("action"), action},
                          {QStringLiteral("name"), name}};

    // QSaveFile: 임시 파일에 다 쓴 뒤 이름을 바꿈 → 실행기가 반쯤 쓴 파일을 읽지 않음
    QSaveFile file(logsPath(kRequestFile));
    if (!file.open(QIODevice::WriteOnly)) {
        emit message(QStringLiteral("요청 파일을 쓰지 못했습니다: %1").arg(file.errorString()), true);
        return false;
    }
    file.write(QJsonDocument(req).toJson(QJsonDocument::Compact));
    if (!file.commit()) {
        emit message(QStringLiteral("요청 파일을 쓰지 못했습니다: %1").arg(file.errorString()), true);
        return false;
    }

    m_pendingId = id;
    m_pendingText = QStringLiteral("%1 %2").arg(name, actionNames().value(action, action));
    m_pendingSinceMs = QDateTime::currentMSecsSinceEpoch();
    emit message(m_pendingText + QStringLiteral(" 요청..."), false);
    return true;
}

void RunnerClient::poll()
{
    RunnerStatus st;
    QFile file(logsPath(kStatusFile));
    if (serverDirOk() && file.open(QIODevice::ReadOnly)) {
        QJsonParseError err{};
        const auto doc = QJsonDocument::fromJson(file.readAll(), &err);
        file.close();
        if (err.error != QJsonParseError::NoError) {
            return;  // 쓰는 중이면 다음 차례에 다시 읽음 (지난 상태 유지)
        }
        const auto root = doc.object();
        const auto runner = root.value(QStringLiteral("runner")).toObject();
        const auto options = runner.value(QStringLiteral("options")).toObject();

        st.fileFound = true;
        st.running = runner.value(QStringLiteral("running")).toBool();
        st.pid = runner.value(QStringLiteral("pid")).toInteger(-1);
        st.started = QDateTime::fromString(runner.value(QStringLiteral("started")).toString(), kTimeFormat);
        st.updated = QDateTime::fromString(runner.value(QStringLiteral("updated")).toString(), kTimeFormat);
        st.hidden = runner.value(QStringLiteral("hidden")).toBool();
        st.brokerExternal = runner.value(QStringLiteral("broker_external")).toBool();
        st.noFake = options.value(QStringLiteral("no_fake")).toBool();
        st.noDb = options.value(QStringLiteral("no_db")).toBool();
        st.estopTest = options.value(QStringLiteral("estop_test")).toBool();

        const double updatedUnix = runner.value(QStringLiteral("updated_unix")).toDouble();
        const double nowUnix = QDateTime::currentMSecsSinceEpoch() / 1000.0;
        st.stale = st.running && (nowUnix - updatedUnix > AppConfig::kRunnerStaleSec);

        const auto programs = root.value(QStringLiteral("programs")).toArray();
        for (const auto &v : programs) {
            const auto o = v.toObject();
            ProgramInfo p;
            p.name = o.value(QStringLiteral("name")).toString();
            p.state = o.value(QStringLiteral("state")).toString();
            p.pid = o.value(QStringLiteral("pid")).toInteger(-1);
            p.started = QDateTime::fromString(o.value(QStringLiteral("started")).toString(), kTimeFormat);
            const auto exit = o.value(QStringLiteral("exit_code"));
            p.hasExitCode = exit.isDouble();
            p.exitCode = exit.toInt();
            p.log = o.value(QStringLiteral("log")).toString();
            st.programs.append(p);
        }

        // 보낸 요청의 결과 확인
        const auto last = root.value(QStringLiteral("last_request")).toObject();
        if (!m_pendingId.isEmpty() && last.value(QStringLiteral("id")).toString() == m_pendingId) {
            const bool ok = last.value(QStringLiteral("ok")).toBool();
            emit message(QStringLiteral("%1: %2").arg(m_pendingText, last.value(QStringLiteral("message")).toString()), !ok);
            m_pendingId.clear();
        }
    }

    // 실행기가 요청을 가져가지 않으면 (꺼졌거나 멈춤) 요청을 거둬들임
    if (!m_pendingId.isEmpty()
        && QDateTime::currentMSecsSinceEpoch() - m_pendingSinceMs > AppConfig::kRequestTimeoutMs) {
        QFile::remove(logsPath(kRequestFile));
        emit message(m_pendingText + QStringLiteral(": 실행기가 응답하지 않아 취소했습니다."), true);
        m_pendingId.clear();
    }

    m_status = st;
    emit statusChanged(m_status);
}
