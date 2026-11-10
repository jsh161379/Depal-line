#include "PortProbe.h"

#include <QTcpSocket>

PortProbe::PortProbe(const QString &host, quint16 port, int intervalMs, QObject *parent)
    : QObject(parent), m_host(host), m_port(port), m_socket(new QTcpSocket(this))
{
    connect(m_socket, &QTcpSocket::connected, this, [this] {
        setState(true);
        m_socket->abort();
    });
    connect(m_socket, &QTcpSocket::errorOccurred, this, [this](QAbstractSocket::SocketError) {
        setState(false);
    });
    connect(&m_timer, &QTimer::timeout, this, &PortProbe::probe);
    m_timer.start(intervalMs);
    probe();
}

void PortProbe::probe()
{
    // 지난번 시도가 아직 끝나지 않았으면 (응답 없음) 닫힘으로 봄
    if (m_socket->state() != QAbstractSocket::UnconnectedState) {
        m_socket->abort();
        setState(false);
    }
    m_socket->connectToHost(m_host, m_port);
}

void PortProbe::setState(bool open)
{
    const int s = open ? 1 : 0;
    if (s == m_state)
        return;
    m_state = s;
    emit stateChanged(open);
}
